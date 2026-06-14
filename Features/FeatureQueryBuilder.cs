using System.Globalization;
using PgDataHub.Config;
using PgDataHub.Metadata;
using Npgsql;
using NpgsqlTypes;

namespace PgDataHub.Features;


public enum FilterOperator
{
    Eq, Neq, Gt, Gte, Lt, Lte,
    In, NotIn,
    IsNull, IsNotNull,
    Like, NotLike,
    ILike, NotILike,
    Between
}

public sealed class PropertyFilter
{
    public required string PropertyName { get; init; }
    public required FilterOperator Operator { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
}


public sealed class FeatureQueryParameters
{
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public IReadOnlyList<string>? Select { get; init; }
    public bool Distinct { get; init; }
    public IReadOnlyList<string>? OrderBy { get; init; }
    public double[]? Bbox { get; init; }
    public int BboxCrsSrid { get; init; } = 4326;

    public int? ResponseCrsSrid { get; init; }

    public string? Datetime { get; init; }
    public bool Count { get; init; }
    public IReadOnlyList<PropertyFilter> Filters { get; init; } = [];
}

public static class ReservedQueryKeys
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "limit", "offset", "select", "distinct", "orderby",
        "bbox", "bbox-crs", "crs", "datetime", "count", "f"
    };
}


public sealed class FeatureQuery
{
    public required CollectionMetadata Collection { get; init; }
    public required string Sql { get; init; }
    public IReadOnlyList<NpgsqlParameter> Parameters { get; init; } = [];

    public const string TotalCountColumn = "__total_count";
}


public sealed class FeatureQueryBuilder
{
    private readonly PgDataHubConfig _config;
    private const int MaxLimit = 10_000;

    public FeatureQueryBuilder(PgDataHubConfig config)
    {
        _config = config;
    }

    public FeatureQuery Build(
        CollectionMetadata collection,
        FeatureQueryParameters? parameters = null)
    {
        parameters ??= new FeatureQueryParameters();

        var sqlParameters = new List<NpgsqlParameter>();

        var columns = BuildColumnList(collection, parameters.Select, parameters.ResponseCrsSrid);

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Collection '{collection.Schema}.{collection.Name}' has no visible/selectable columns.");
        }

        var distinct = parameters.Distinct ? "distinct " : "";
        var selectClause = string.Join(",\n    ", columns);

        if (parameters.Count)
        {
            selectClause += $",\n    count(*) over() as \"{FeatureQuery.TotalCountColumn}\"";
        }

        var whereClauses = new List<string>();

        AppendBboxFilter(collection, parameters.Bbox, parameters.BboxCrsSrid, whereClauses, sqlParameters);

        AppendPropertyFilters(collection, parameters.Filters, whereClauses, sqlParameters);

        var whereClause = whereClauses.Count > 0
            ? $"\nwhere {string.Join(" and ", whereClauses)}"
            : string.Empty;

        var orderByClause = BuildOrderByClause(collection, parameters.OrderBy);

        var limit = ResolveLimit(parameters.Limit);
        var offset = ResolveOffset(parameters.Offset);

        var sql =
$"""
select
    {distinct}{selectClause}
from "{collection.Schema}"."{collection.Name}" t{whereClause}{orderByClause}
limit {limit}
offset {offset}
""";

        return new FeatureQuery
        {
            Collection = collection,
            Sql = sql,
            Parameters = sqlParameters
        };
    }


    private static List<string> BuildColumnList(
        CollectionMetadata collection,
        IReadOnlyList<string>? select,
        int? responseCrsSrid)
    {
        var columns = new List<string>();

        IEnumerable<ColumnMetadata> source;

        if (select is { Count: > 0 })
        {
            var byName = collection.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<ColumnMetadata>();

            foreach (var name in select)
            {
                if (byName.TryGetValue(name, out var col))
                    ordered.Add(col);
            }

            source = ordered;
        }
        else
        {
            source = collection.Columns;
        }

        var geomSrid = collection.Srid ?? 4326;
        var targetSrid = responseCrsSrid ?? geomSrid;

        foreach (var column in source)
        {
            if (collection.IsSpatial &&
                collection.GeometryColumn is not null &&
                column.Name.Equals(collection.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                var geomExpr = $"t.\"{column.Name}\"";

                if (targetSrid != geomSrid)
                {
                    geomExpr = $"ST_Transform({geomExpr}, {targetSrid})";
                }

                columns.Add($"ST_AsGeoJSON({geomExpr})::jsonb as \"{column.Name}\"");
                continue;
            }

            columns.Add($"t.\"{column.Name}\"");
        }

        return columns;
    }

    
    private void AppendBboxFilter(
        CollectionMetadata collection,
        double[]? bbox,
        int crsSrid,
        List<string> whereClauses,
        List<NpgsqlParameter> sqlParameters)
    {
        if (bbox is null)
            return;

        if (!collection.IsSpatial || collection.GeometryColumn is null)
        {
            throw new InvalidOperationException(
                $"Collection '{collection.Schema}.{collection.Name}' is not spatial; 'bbox' is not supported.");
        }

        if (bbox.Length != 4)
        {
            throw new ArgumentException("bbox must contain exactly 4 values: minx,miny,maxx,maxy.");
        }

        var p = "@bbox";

        // bbox geldiği CRS'te envelope oluştur, gerekirse geometri SRID'ine transform et
        var envelope = $"ST_MakeEnvelope({p}0, {p}1, {p}2, {p}3, {crsSrid})";

        var geomSrid = collection.Srid ?? 4326;

        var envelopeExpr = geomSrid == crsSrid
            ? envelope
            : $"ST_Transform({envelope}, {geomSrid})";

        whereClauses.Add($"t.\"{collection.GeometryColumn}\" && {envelopeExpr}");

        for (var i = 0; i < 4; i++)
        {
            sqlParameters.Add(new NpgsqlParameter($"{p}{i}", NpgsqlDbType.Double) { Value = bbox[i] });
        }
    }

    // ----------------------------------------------------------------
    // DATETIME
    // ----------------------------------------------------------------
    //private void AppendDatetimeFilter(
    //    CollectionMetadata collection,
    //    string? datetime,
    //    List<string> whereClauses,
    //    List<NpgsqlParameter> sqlParameters)
    //{
    //    if (string.IsNullOrWhiteSpace(datetime))
    //        return;

    //    var dateColumn = collection.PrimaryDatetimeColumn;

    //    if (dateColumn is null)
    //    {
    //        throw new InvalidOperationException(
    //            $"Collection '{collection.Schema}.{collection.Name}' has no datetime column.");
    //    }

    //    var idx = sqlParameters.Count;

    //    if (datetime.Contains('/'))
    //    {
    //        var parts = datetime.Split('/', 2);
    //        var startRaw = parts[0];
    //        var endRaw = parts[1];

    //        if (startRaw != "..")
    //        {
    //            var p = $"@dt_s{idx}";
    //            whereClauses.Add($"t.\"{dateColumn}\" >= {p}");
    //            sqlParameters.Add(new NpgsqlParameter(p, NpgsqlDbType.TimestampTz)
    //            {
    //                Value = ParseDateTime(startRaw, "datetime start")
    //            });
    //        }

    //        if (endRaw != "..")
    //        {
    //            var p = $"@dt_e{idx}";
    //            whereClauses.Add($"t.\"{dateColumn}\" <= {p}");
    //            sqlParameters.Add(new NpgsqlParameter(p, NpgsqlDbType.TimestampTz)
    //            {
    //                Value = ParseDateTime(endRaw, "datetime end")
    //            });
    //        }
    //    }
    //    else
    //    {
    //        var p = $"@dt{idx}";
    //        whereClauses.Add($"t.\"{dateColumn}\" = {p}");
    //        sqlParameters.Add(new NpgsqlParameter(p, NpgsqlDbType.TimestampTz)
    //        {
    //            Value = ParseDateTime(datetime, "datetime")
    //        });
    //    }
    //}

    private static DateTimeOffset ParseDateTime(string raw, string ctx)
    {
        if (!DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            throw new ArgumentException($"Invalid {ctx} value: '{raw}'. Expected ISO-8601.");
        }

        return result;
    }


    private void AppendPropertyFilters(
        CollectionMetadata collection,
        IReadOnlyList<PropertyFilter> filters,
        List<string> whereClauses,
        List<NpgsqlParameter> sqlParameters)
    {
        if (filters.Count == 0)
            return;

        var columnsByName = collection.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var filter in filters)
        {
            if (!columnsByName.TryGetValue(filter.PropertyName, out var column))
            {
                throw new ArgumentException(
                    $"Unknown property '{filter.PropertyName}' for collection '{collection.Schema}.{collection.Name}'.");
            }

            if (collection.IsSpatial &&
                collection.GeometryColumn is not null &&
                column.Name.Equals(collection.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Geometry column '{column.Name}' cannot be filtered directly.");
            }

            var c = $"t.\"{column.Name}\"";
            var idx = sqlParameters.Count;

            switch (filter.Operator)
            {
                case FilterOperator.Eq:
                    whereClauses.Add($"{c} = {Add(c, 0)}");
                    break;

                case FilterOperator.Neq:
                    whereClauses.Add($"{c} != {Add(c, 0)}");
                    break;

                case FilterOperator.Gt:
                    whereClauses.Add($"{c} > {Add(c, 0)}");
                    break;

                case FilterOperator.Gte:
                    whereClauses.Add($"{c} >= {Add(c, 0)}");
                    break;

                case FilterOperator.Lt:
                    whereClauses.Add($"{c} < {Add(c, 0)}");
                    break;

                case FilterOperator.Lte:
                    whereClauses.Add($"{c} <= {Add(c, 0)}");
                    break;

                case FilterOperator.In:
                case FilterOperator.NotIn:
                    {
                        if (filter.Values.Count == 0)
                            throw new ArgumentException($"'in'/'not in' for '{filter.PropertyName}' requires values.");

                        var placeholders = new List<string>(filter.Values.Count);

                        for (var i = 0; i < filter.Values.Count; i++)
                        {
                            var p = $"@p{idx}_{i}";
                            placeholders.Add(p);
                            sqlParameters.Add(CreateParameter(p, column, filter.Values[i]));
                        }

                        var op = filter.Operator == FilterOperator.In ? "in" : "not in";
                        whereClauses.Add($"{c} {op} ({string.Join(", ", placeholders)})");
                        break;
                    }

                case FilterOperator.IsNull:
                    whereClauses.Add($"{c} is null");
                    break;

                case FilterOperator.IsNotNull:
                    whereClauses.Add($"{c} is not null");
                    break;

                case FilterOperator.Like:
                case FilterOperator.NotLike:
                    {
                        var raw = Single(filter);

                        var pattern = raw
                            .Replace("%", "\\%")
                            .Replace("_", "\\_")
                            .Replace('*', '%')
                            .Replace('?', '_');

                        var p = $"@p{idx}";
                        sqlParameters.Add(new NpgsqlParameter(p, NpgsqlDbType.Text) { Value = pattern });

                        var op = filter.Operator == FilterOperator.Like ? "ilike" : "not ilike";
                        whereClauses.Add($"{c} {op} {p}");
                        break;
                    }

                case FilterOperator.ILike:
                case FilterOperator.NotILike:
                    {
                        var raw = Single(filter);

                        var p = $"@p{idx}";
                        sqlParameters.Add(new NpgsqlParameter(p, NpgsqlDbType.Text) { Value = raw });

                        var op = filter.Operator == FilterOperator.ILike ? "ilike" : "not ilike";
                        whereClauses.Add($"{c} {op} {p}");
                        break;
                    }

                case FilterOperator.Between:
                    {
                        if (filter.Values.Count != 2)
                            throw new ArgumentException($"'between' for '{filter.PropertyName}' requires 2 values.");

                        var pLow = $"@p{idx}_lo";
                        var pHigh = $"@p{idx}_hi";

                        sqlParameters.Add(CreateParameter(pLow, column, filter.Values[0]));
                        sqlParameters.Add(CreateParameter(pHigh, column, filter.Values[1]));

                        whereClauses.Add($"{c} between {pLow} and {pHigh}");
                        break;
                    }

                default:
                    throw new NotSupportedException($"Operator '{filter.Operator}' not supported.");
            }

            continue;

            string Add(string _, int valueIndex)
            {
                var p = $"@p{idx}";
                sqlParameters.Add(CreateParameter(p, column, filter.Values[valueIndex]));
                return p;
            }
        }
    }

    private static string Single(PropertyFilter f)
    {
        if (f.Values.Count != 1)
            throw new ArgumentException($"'{f.Operator}' for '{f.PropertyName}' requires exactly 1 value.");
        return f.Values[0];
    }


    private static NpgsqlParameter CreateParameter(
        string name,
        ColumnMetadata column,
        string raw)
    {
        var type =
            column.Type
                .ToLowerInvariant();

        try
        {
            if (type.Contains("int"))
            {
                return new NpgsqlParameter(
                    name,
                    long.Parse(
                        raw,
                        CultureInfo.InvariantCulture));
            }

            if (type.StartsWith("numeric") ||
                type.StartsWith("decimal"))
            {
                return new NpgsqlParameter(
                    name,
                    decimal.Parse(
                        raw,
                        CultureInfo.InvariantCulture));
            }

            if (type == "real")
            {
                return new NpgsqlParameter(
                    name,
                    float.Parse(
                        raw,
                        CultureInfo.InvariantCulture));
            }

            if (type == "double precision")
            {
                return new NpgsqlParameter(
                    name,
                    double.Parse(
                        raw,
                        CultureInfo.InvariantCulture));
            }

            if (type == "boolean")
            {
                return new NpgsqlParameter(
                    name,
                    bool.Parse(raw));
            }

            if (type == "date")
            {
                return new NpgsqlParameter(
                    name,
                    DateTime.Parse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None)
                        .Date);
            }

            if (type.Contains("timestamp"))
            {
                return new NpgsqlParameter(
                    name,
                    DateTimeOffset.Parse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal |
                        DateTimeStyles.AdjustToUniversal));
            }

            if (type == "uuid")
            {
                return new NpgsqlParameter(
                    name,
                    Guid.Parse(raw));
            }
        }
        catch
        {
          
        }

        return new NpgsqlParameter(
            name,
            raw);
    }


    private static string BuildOrderByClause(CollectionMetadata collection, IReadOnlyList<string>? orderBy)
    {
        if (orderBy is null || orderBy.Count == 0)
            return string.Empty;

        var byName = collection.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var raw in orderBy)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var desc = raw.StartsWith('-');
            var name = desc ? raw[1..] : raw;

            if (name.StartsWith('+'))
                name = name[1..];

            if (!byName.TryGetValue(name, out var column))
                throw new ArgumentException($"Unknown property '{name}' in 'orderby'.");

            parts.Add($"t.\"{column.Name}\" {(desc ? "desc" : "asc")}");
        }

        return parts.Count == 0 ? string.Empty : $"\norder by {string.Join(", ", parts)}";
    }


    private static int ResolveOffset(int? requested)
    {
        if (requested is null)
            return 0;

        if (requested.Value < 0)
            throw new ArgumentException("'offset' must be >= 0.");

        return requested.Value;
    }
    private int ResolveLimit(int? requested)
    {
        var defaultLimit = _config.Feature.DefaultLimit;

        if (requested is null)
            return defaultLimit;

        if (requested.Value < 0)
            throw new ArgumentException("'limit' must be >= 0.");

        return Math.Min(requested.Value, MaxLimit);
    }
}


public static class FeatureQueryParametersParser
{
    public static FeatureQueryParameters Parse(IDictionary<string, string?> query)
    {
        int? limit = ParseInt(query, "limit");
        int? offset = ParseInt(query, "offset");

        var select = ParseList(query, "select");

        if (select is { Count: 1 } && select[0] == "*")
        {
            select = null;
        }
        var orderBy = ParseList(query, "orderby");

        var distinct = ParseBool(query, "distinct");
        var count = ParseBool(query, "count");
        
        double[]? bbox = null;
        if (query.TryGetValue("bbox", out var bboxRaw) && !string.IsNullOrWhiteSpace(bboxRaw))
        {
            bbox = bboxRaw.Split(',')
                .Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture))
                .ToArray();
        }

        var bboxCrs = 4326;
        if (query.TryGetValue("bbox-crs", out var bboxCrsRaw) && !string.IsNullOrWhiteSpace(bboxCrsRaw))
        {
            bboxCrs = ParseCrs(bboxCrsRaw, "bbox-crs");
        }

        int? responseCrs = null;
        if (query.TryGetValue("crs", out var crsRaw) && !string.IsNullOrWhiteSpace(crsRaw))
        {
            responseCrs = ParseCrs(crsRaw, "crs");
        }

        string? datetime = null;
        if (query.TryGetValue("datetime", out var dtRaw) && !string.IsNullOrWhiteSpace(dtRaw))
        {
            datetime = dtRaw;
        }

        var filters = new List<PropertyFilter>();

        foreach (var (rawKey, rawValue) in query)
        {
            if (rawValue is null)
                continue;

            if (ReservedQueryKeys.Names.Contains(rawKey))
                continue;

            var key = rawKey;

            if (rawKey.EndsWith("!"))
                key += "=";

            else if (rawKey.EndsWith(">"))
                key += "=";

            else if (rawKey.EndsWith("<"))
                key += "=";

            else if (rawKey.EndsWith("~"))
                key += "=";

            var (property, op, inlineValue) = SplitOperator(key);

            var value = string.IsNullOrEmpty(inlineValue)
                ? rawValue
                : inlineValue;

            filters.Add(ParsePropertyFilter(property, op, value ?? ""));
        }

        return new FeatureQueryParameters
        {
            Limit = limit,
            Offset = offset,
            Select = select,
            Distinct = distinct,
            OrderBy = orderBy,
            Bbox = bbox,
            BboxCrsSrid = bboxCrs,
            ResponseCrsSrid = responseCrs,
            Datetime = datetime,
            Count = count,
            Filters = filters
        };
    }

    private static (string Property, string Op, string? Value) SplitOperator(string key)
    {
        string[] ops =
        [
            "!~=",
        "~=",
        ">=",
        "<=",
        "!=",
        "==",
        ">",
        "<",
        "="
        ];

        foreach (var op in ops)
        {
            var idx = key.IndexOf(op, StringComparison.Ordinal);

            if (idx <= 0)
                continue;

            return (
                key[..idx],
                op,
                key[(idx + op.Length)..]
            );
        }

        return (key, "=", null);
    }

    private static PropertyFilter ParsePropertyFilter(string property, string op, string rawValue)
    {
        if (rawValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            var isNot = op is "!=" or "<>";

            return new PropertyFilter
            {
                PropertyName = property,
                Operator = isNot ? FilterOperator.IsNotNull : FilterOperator.IsNull,
                Values = []
            };
        }

        switch (op)
        {
            case "=":
            case "==":
                if (rawValue.Contains(".."))
                    return BetweenFilter(property, rawValue);

                if (rawValue.Contains(','))
                    return new PropertyFilter
                    {
                        PropertyName = property,
                        Operator = FilterOperator.In,
                        Values = SplitValues(rawValue)
                    };

                if (rawValue.Contains('*') || rawValue.Contains('?'))
                    return new PropertyFilter
                    {
                        PropertyName = property,
                        Operator = FilterOperator.Like,
                        Values = [rawValue]
                    };

                return new PropertyFilter
                {
                    PropertyName = property,
                    Operator = FilterOperator.Eq,
                    Values = [rawValue]
                };

            case "!=":
            case "<>":
                if (rawValue.Contains(','))
                    return new PropertyFilter
                    {
                        PropertyName = property,
                        Operator = FilterOperator.NotIn,
                        Values = SplitValues(rawValue)
                    };

                if (rawValue.Contains('*') || rawValue.Contains('?'))
                    return new PropertyFilter
                    {
                        PropertyName = property,
                        Operator = FilterOperator.NotLike,
                        Values = [rawValue]
                    };

                return new PropertyFilter
                {
                    PropertyName = property,
                    Operator = FilterOperator.Neq,
                    Values = [rawValue]
                };

            case ">":
                return new PropertyFilter { PropertyName = property, Operator = FilterOperator.Gt, Values = [rawValue] };

            case ">=":
                return new PropertyFilter { PropertyName = property, Operator = FilterOperator.Gte, Values = [rawValue] };

            case "<":
                return new PropertyFilter { PropertyName = property, Operator = FilterOperator.Lt, Values = [rawValue] };

            case "<=":
                return new PropertyFilter { PropertyName = property, Operator = FilterOperator.Lte, Values = [rawValue] };

            case "~=":
                return new PropertyFilter { PropertyName = property, Operator = FilterOperator.ILike, Values = [rawValue] };

            case "!~=":
                return new PropertyFilter { PropertyName = property, Operator = FilterOperator.NotILike, Values = [rawValue] };

            default:
                throw new ArgumentException($"Unsupported operator '{op}' for property '{property}'.");
        }
    }

    private static PropertyFilter BetweenFilter(string property, string rawValue)
    {
        var parts = rawValue.Split("..", 2);

        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw new ArgumentException(
                $"Invalid 'between' value '{rawValue}' for property '{property}'. Expected 'min..max'.");
        }

        return new PropertyFilter
        {
            PropertyName = property,
            Operator = FilterOperator.Between,
            Values = [parts[0].Trim(), parts[1].Trim()]
        };
    }

    private static int ParseCrs(string raw, string paramName)
    {
        // "EPSG:4326", "http://www.opengis.net/def/crs/EPSG/0/4326", "4326"
        var lastSegment = raw.Split('/', ':')
            .LastOrDefault(s => !string.IsNullOrWhiteSpace(s));

        if (lastSegment is not null && int.TryParse(lastSegment, out var srid))
            return srid;

        throw new ArgumentException($"Invalid '{paramName}' value: '{raw}'.");
    }

    private static bool ParseBool(IDictionary<string, string?> query, string key)
    {
        return query.TryGetValue(key, out var raw) &&
               bool.TryParse(raw, out var val) &&
               val;
    }

    private static int? ParseInt(IDictionary<string, string?> query, string key)
    {
        if (query.TryGetValue(key, out var raw) &&
            !string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static IReadOnlyList<string>? ParseList(IDictionary<string, string?> query, string key)
    {
        if (!query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static List<string> SplitValues(string raw)
    {
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}