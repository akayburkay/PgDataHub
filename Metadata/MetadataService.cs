namespace PgDataHub.Metadata;

using Npgsql;
using PgDataHub.Config;
using Microsoft.Extensions.Logging;


internal enum FilterMode { Passthrough, Whitelist, Blacklist }

internal static class CollectionOverrideApplier
{
    internal static CollectionMetadata Apply(
        CollectionMetadata source,
        CollectionConfig? overrideConfig,
        ILogger logger)
        => Apply(
            source,
            overrideConfig?.Feature.Allow ?? [],
            overrideConfig?.Feature.Hide ?? [],
            validateColumns: overrideConfig is not null,
            logger);

    internal static CollectionMetadata Apply(
        CollectionMetadata source,
        IReadOnlyList<string> allow,
        IReadOnlyList<string> hide,
        bool validateColumns,
        ILogger logger)
    {
        if (validateColumns)
        {
            var actual = new HashSet<string>(
                source.Columns.Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var n in allow.Where(n => !actual.Contains(n)))
                logger.LogError("[Security] 'allow' unknown column {Schema}.{Table}.{Col} — will never appear",
                    source.Schema, source.Name, n);

            foreach (var n in hide.Where(n => !actual.Contains(n)))
                logger.LogError("[Security] 'hide' unknown column {Schema}.{Table}.{Col} — may stay VISIBLE",
                    source.Schema, source.Name, n);
        }

        var mode = allow.Count > 0 ? FilterMode.Whitelist
                 : hide.Count > 0 ? FilterMode.Blacklist
                                   : FilterMode.Passthrough;

        var allowSet = mode == FilterMode.Whitelist
            ? new HashSet<string>(allow, StringComparer.OrdinalIgnoreCase) : null;
        var hideSet = mode == FilterMode.Blacklist
            ? new HashSet<string>(hide, StringComparer.OrdinalIgnoreCase) : null;

        var cols = source.Columns
            .Where(col =>
            {
                bool visible = mode switch
                {
                    FilterMode.Whitelist => allowSet!.Contains(col.Name) &&
                                            !hide.Contains(col.Name, StringComparer.OrdinalIgnoreCase),
                    FilterMode.Blacklist => !hideSet!.Contains(col.Name),
                    FilterMode.Passthrough => true,
                    _ => throw new InvalidOperationException($"Unknown FilterMode: {mode}")
                };
                if (!visible)
                    logger.LogWarning("[Security] Column hidden — {Schema}.{Table}.{Col} mode:{Mode}",
                        source.Schema, source.Name, col.Name, mode);
                return visible;
            })
            .Select(col => new ColumnMetadata
            {
                Name = col.Name,
                Type = col.Type,
                Nullable = col.Nullable
            })
            .ToList();

        var visible = new HashSet<string>(cols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        var geom = source.GeometryColumn is not null && visible.Contains(source.GeometryColumn)
            ? source.GeometryColumn : null;
        var pk = source.PrimaryKey is not null && visible.Contains(source.PrimaryKey)
            ? source.PrimaryKey : null;

        if (source.GeometryColumn is not null && geom is null)
            logger.LogWarning("[Security] Geometry column hidden — {Schema}.{Table}.{Col}",
                source.Schema, source.Name, source.GeometryColumn);
        if (source.PrimaryKey is not null && pk is null)
            logger.LogWarning("[Security] PK column hidden — {Schema}.{Table}.{Col}",
                source.Schema, source.Name, source.PrimaryKey);

        if (mode != FilterMode.Passthrough)
            logger.LogInformation("[Security] Filter applied — {Schema}.{Table} mode:{Mode} visible:{V} hidden:{H}",
                source.Schema, source.Name, mode, cols.Count, source.Columns.Count - cols.Count);

        return new CollectionMetadata
        {
            Schema = source.Schema,
            Name = source.Name,
            Type = source.Type,
            IsSpatial = geom is not null,
            GeometryColumn = geom,
            GeometryType = geom is not null ? source.GeometryType : null,
            Srid = geom is not null ? source.Srid : null,
            PrimaryKey = pk,
            Columns = cols,
            PolicyColumns = source.PolicyColumns ?? source.Columns,
            RowFilters = source.RowFilters
        };
    }
}


public sealed class MetadataService
{
    private readonly PgDataHubConfig _config;
    private readonly ILogger<MetadataService> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private MetadataCache? _cache;

    public MetadataService(PgDataHubConfig config, ILogger<MetadataService> logger)
    {
        _config = config;
        _logger = logger;
    }


    public async Task RefreshAsync(CancellationToken ct = default)
        => await GetCacheAsync(refresh: true, ct);

    public async Task<IReadOnlyList<CollectionMetadata>> GetCollectionsAsync(CancellationToken ct = default)
        => (await GetCacheAsync(false, ct)).Collections;

    public async Task<FunctionMetadata?> GetFunctionAsync(
    string schema,
    string name,
    CancellationToken ct = default)
    {
        var cache = await GetCacheAsync(false, ct);
        return cache.FunctionLookup.TryGetValue(Key(schema, name), out var matches) && matches.Count == 1
            ? matches[0]
            : null;
    }

    public async Task<FunctionMetadata?> GetFunctionAsync(uint oid, CancellationToken ct = default)
    {
        (await GetCacheAsync(false, ct)).FunctionOidLookup.TryGetValue(oid, out var function);
        return function;
    }
    public async Task<IReadOnlyList<FunctionMetadata>> GetFunctionsAsync(
    CancellationToken ct = default)
    => (await GetCacheAsync(false, ct)).Functions;

    public async Task<object> GetAdminLookupAsync(CancellationToken ct = default)
    {
        var collections = await LoadCollectionsAsync(ct);
        var functions = await LoadFunctionsAsync(ct, includeVolatile: true);
        return new
        {
            collections = collections.Select(c => new
            {
                id = Key(c.Schema, c.Name), c.Schema, c.Name, c.Type,
                columns = c.Columns.Select(column => column.Name).ToList()
            }).ToList(),
            functions = functions.Select(f => new { f.Oid, f.Schema, f.Name, f.Signature }).ToList()
        };
    }

    public async Task<object> GetListenerStatusAsync(CancellationToken ct = default)
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select
              exists(select 1 from pg_event_trigger where evtname = 'pgdatahub_metadata_trigger'),
              to_regprocedure('public.pgdatahub_metadata_notify()') is not null
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new { triggerInstalled = reader.GetBoolean(0), functionInstalled = reader.GetBoolean(1), channel = "metadata_changed" };
    }
    public async Task<CollectionMetadata?> GetCollectionAsync(string schema, string table, CancellationToken ct = default)
    {
        (await GetCacheAsync(false, ct)).CollectionLookup.TryGetValue(Key(schema, table), out var c);
        return c;
    }


    private async Task<MetadataCache> GetCacheAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _cache is not null) return _cache;

        await _sync.WaitAsync(ct);
        try
        {
            if (!refresh && _cache is not null) return _cache;
            return _cache = await BuildCacheAsync(ct);
        }
        finally { _sync.Release(); }
    }

    private async Task<MetadataCache> BuildCacheAsync(CancellationToken ct)
    {
        var raw = await LoadCollectionsAsync(ct);
        var filtered = raw
            .Where(IsCollectionAllowed)
            .Select(ApplySecurityFilter)
            .ToList();
        var functions = await LoadFunctionsAsync(ct);

        functions = functions
            .Where(IsFunctionAllowed)
            .ToList();

        var functionColumns =
    await LoadFunctionColumnsAsync(ct);

        var functionParameters =
    await LoadFunctionParametersAsync(ct);

        foreach (var function in functions)
        {
            if (functionColumns.TryGetValue(
                function.Oid,
                out var columns))
            {
                function.Columns = columns;
            }

            if (functionParameters.TryGetValue(
                function.Oid,
                out var parameters))
            {
                function.Parameters = parameters;
            }
        }

        return new MetadataCache
        {
            Collections = filtered,
            Functions = functions,
            CollectionLookup = filtered.ToDictionary(x => Key(x.Schema, x.Name), StringComparer.OrdinalIgnoreCase),
            FunctionLookup = functions.GroupBy(x => Key(x.Schema, x.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<FunctionMetadata>)g.ToList(), StringComparer.OrdinalIgnoreCase),
            FunctionOidLookup = functions.ToDictionary(x => x.Oid)
        };
    }


    private bool IsCollectionAllowed(CollectionMetadata c)
    {
        if (_config.Collections.Exclude.Any(p => Match(c.Name, p)))
        {
            _logger.LogDebug("[Metadata] Collection excluded: {Schema}.{Table}", c.Schema, c.Name);
            return false;
        }

        var inc = _config.Collections.Include;
        return inc.Count == 0 || inc.Contains("*") || inc.Any(p => Match(c.Name, p));
    }

    private bool IsFunctionAllowed(FunctionMetadata f)
    {
        var fullName = $"{f.Schema}.{f.Name}";

        if (f.IsVolatile && !_config.Function.VolatileInclude.Any(pattern =>
            Match(f.Name, pattern) || Match(fullName, pattern) || Match(f.Signature, pattern)))
            return false;

        if (_config.Function.Exclude.Any(x =>
            Match(fullName, x) || Match(f.Signature, x)))
        {
            _logger.LogDebug(
                "[Metadata] Function excluded: {Function}",
                fullName);

            return false;
        }

        var include = _config.Function.Include;

        if (include.Count == 0)
        {
            return true;
        }

        return include.Any(pattern =>
            Match(f.Name, pattern) || Match(fullName, pattern) || Match(f.Signature, pattern));
    }

    private static bool Match(string value, string pattern)
        => Glob.Match(value, pattern);


    private CollectionMetadata ApplySecurityFilter(CollectionMetadata raw)
    {
        _config.Tables.TryGetValue(Key(raw.Schema, raw.Name), out var cfg);

        if (cfg is null)
            _logger.LogDebug("[Security] No override — {Schema}.{Table} — all columns visible",
                raw.Schema, raw.Name);

        return CollectionOverrideApplier.Apply(raw, cfg, _logger);
    }


    private async Task<IReadOnlyList<CollectionMetadata>> LoadCollectionsAsync(CancellationToken ct)
    {
        await using var conn = await Db.OpenConnectionAsync(ct);
        var list = new List<CollectionMetadata>();

        await using (var cmd = new NpgsqlCommand(MetadataSql.Collections, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                list.Add(new CollectionMetadata
                {
                    Schema = r.GetString(0),
                    Name = r.GetString(1),
                    Type = r.GetString(2),
                    GeometryColumn = r.IsDBNull(3) ? null : r.GetString(3),
                    GeometryType = r.IsDBNull(4) ? null : r.GetString(4),
                    Srid = r.IsDBNull(5) ? null : r.GetInt32(5),
                    PrimaryKey = r.IsDBNull(6) ? null : r.GetString(6),
                    IsSpatial = !r.IsDBNull(3)
                });

        var colLookup = new Dictionary<string, List<ColumnMetadata>>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = new NpgsqlCommand(MetadataSql.Columns, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var k = Key(r.GetString(0), r.GetString(1));
                if (!colLookup.TryGetValue(k, out var cols)) colLookup[k] = cols = [];
                cols.Add(new ColumnMetadata { Name = r.GetString(2), Type = r.GetString(3), Nullable = r.GetBoolean(4) });
            }

        foreach (var c in list)
            if (colLookup.TryGetValue(Key(c.Schema, c.Name), out var cols))
                c.Columns = cols;

        // geometry_columns reports SRID 0 for unconstrained geometry columns even
        // when the stored geometries have a valid SRID. Use a stored value before
        // building ST_Transform expressions for these collections.
        foreach (var c in list.Where(c => c.IsSpatial && c.Srid == 0 && c.GeometryColumn is not null))
        {
            static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var sql = $"select ST_SRID({Q(c.GeometryColumn!)}) from {Q(c.Schema)}.{Q(c.Name)} " +
                      $"where {Q(c.GeometryColumn!)} is not null limit 1";
            await using var cmd = new NpgsqlCommand(sql, conn);
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is int srid && srid > 0)
                c.Srid = srid;
        }

        return list;
    }

    private async Task<IReadOnlyList<FunctionMetadata>> LoadFunctionsAsync(CancellationToken ct, bool includeVolatile = false)
    {
        await using var conn = await Db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(MetadataSql.Functions, conn);
        cmd.Parameters.AddWithValue("include_volatile", includeVolatile || _config.Function.VolatileInclude.Count > 0);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var list = new List<FunctionMetadata>();

        while (await r.ReadAsync(ct))
        {
            list.Add(new FunctionMetadata
            {
                Oid = r.GetFieldValue<uint>(0),
                Schema = r.GetString(1),
                Name = r.GetString(2),
                Signature = r.GetString(3),
                ReturnType = Enum.Parse<FunctionReturnType>(r.GetString(4), true),
                Description = r.IsDBNull(5) ? "" : r.GetString(5),
                Volatility = r.GetString(6) switch
                {
                    "s" => FunctionVolatility.Stable,
                    "i" => FunctionVolatility.Immutable,
                    _ => FunctionVolatility.Volatile
                }
            });
        }

        return list;
    }

    private async Task<Dictionary<uint, List<FunctionColumnMetadata>>>
    LoadFunctionColumnsAsync(
        CancellationToken ct)
    {
        await using var conn = await Db.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            MetadataSql.FunctionColumns,
            conn);

        await using var r =
            await cmd.ExecuteReaderAsync(ct);

        var lookup =
            new Dictionary<uint, List<FunctionColumnMetadata>>();

        while (await r.ReadAsync(ct))
        {
            var key = r.GetFieldValue<uint>(0);

            if (!lookup.TryGetValue(key, out var columns))
            {
                columns = [];
                lookup[key] = columns;
            }

            columns.Add(new FunctionColumnMetadata
            {
                Name = r.GetString(3),
                Type = r.GetString(4)
            });
        }

        return lookup;
    }

    private async Task<Dictionary<uint, List<FunctionParameterMetadata>>>
    LoadFunctionParametersAsync(
        CancellationToken ct)
    {
        await using var conn = await Db.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            MetadataSql.FunctionParameters,
            conn);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        var lookup =
            new Dictionary<uint, List<FunctionParameterMetadata>>();

        while (await r.ReadAsync(ct))
        {
            var key = r.GetFieldValue<uint>(0);

            if (!lookup.TryGetValue(key, out var parameters))
            {
                parameters = [];
                lookup[key] = parameters;
            }

            parameters.Add(new FunctionParameterMetadata
            {
                Name = r.GetString(3),
                Type = r.GetString(4)
            });
        }

        return lookup;
    }


    private static string Key(string schema, string table) => $"{schema}.{table}";


    private sealed class MetadataCache
    {
        public IReadOnlyList<CollectionMetadata> Collections { get; init; } = [];

        public IReadOnlyList<FunctionMetadata> Functions { get; init; } = [];

        public IReadOnlyDictionary<string, CollectionMetadata> CollectionLookup { get; init; }
            = new Dictionary<string, CollectionMetadata>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, IReadOnlyList<FunctionMetadata>> FunctionLookup { get; init; }
            = new Dictionary<string, IReadOnlyList<FunctionMetadata>>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<uint, FunctionMetadata> FunctionOidLookup { get; init; }
            = new Dictionary<uint, FunctionMetadata>();
    }
}
