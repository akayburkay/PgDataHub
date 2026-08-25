using Microsoft.Extensions.Logging.Abstractions;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Features;
using PgDataHub.Metadata;

var tests = new (string Name, Action Run)[]
{
    ("glob supports wildcards anywhere", GlobWildcards),
    ("invalid scalar query values are rejected", InvalidQueryValues),
    ("configured max limit is enforced", ConfiguredMaxLimit),
    ("typed filter values are rejected", InvalidTypedFilters),
    ("config allow and hide are both applied", ConfigAllowAndHide),
    ("all matching auth column patterns are merged", AuthPatternsMerge),
    ("any matching auth hide rule wins", AuthHideWins),
    ("metadata SQL handles OIDs, ordinary IN args and duplicate joins", MetadataSqlShape),
    ("config writer preserves unknown content and writes atomically", ConfigWriterPreservesContent)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void GlobWildcards()
{
    Assert(Glob.Match("public.get_roads", "*.get_*"), "middle wildcard");
    Assert(Glob.Match("public.fn1", "public.fn?"), "single-character wildcard");
    Assert(!Glob.Match("private.fn1", "public.*"), "schema mismatch");
}

static void InvalidQueryValues()
{
    Throws<ArgumentException>(() => FeatureQueryParametersParser.Parse(Query(("limit", "many"))));
    Throws<ArgumentException>(() => FeatureQueryParametersParser.Parse(Query(("distinct", "yes"))));
    Throws<ArgumentException>(() => FeatureQueryParametersParser.Parse(Query(("bbox", "1,2,nope,4"))));
    Throws<ArgumentException>(() => FeatureQueryParametersParser.Parse(Query(("bbox", "4,2,1,3"))));
}

static void ConfiguredMaxLimit()
{
    var config = new PgDataHubConfig { Feature = new FeatureConfig { DefaultLimit = 50, MaxLimit = 7 } };
    var sql = new FeatureQueryBuilder(config).Build(Collection(), new FeatureQueryParameters { Limit = 99 }).Sql;
    Assert(sql.Contains("limit 7", StringComparison.Ordinal), sql);
}

static void InvalidTypedFilters()
{
    foreach (var (type, value) in new[]
    {
        ("integer", "x"), ("numeric", "x"), ("boolean", "yes"),
        ("uuid", "x"), ("date", "not-a-date"), ("timestamp with time zone", "not-a-date")
    })
    {
        var collection = Collection();
        collection.Columns[0].Type = type;
        Throws<ArgumentException>(() => new FeatureQueryBuilder(new PgDataHubConfig()).Build(
            collection,
            new FeatureQueryParameters
            {
                Filters = [new PropertyFilter { PropertyName = "id", Operator = FilterOperator.Eq, Values = [value] }]
            }));
    }
}

static void ConfigAllowAndHide()
{
    var filtered = CollectionOverrideApplier.Apply(
        CollectionWith("id", "secret", "name"), ["id", "secret"], ["secret"], true,
        NullLogger.Instance);
    Assert(filtered.Columns.Select(c => c.Name).SequenceEqual(["id"]), "hide must win after allow");
}

static void AuthPatternsMerge()
{
    var config = new PgDataHubConfig
    {
        Roles =
        [
            new AuthRoleConfig
            {
                Name = "reader",
                CollectionsAllow = ["public.*"],
                ColumnRules = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["public.*"] = new AuthColumnRuleConfig { Allow = ["id", "name"], Hide = ["secret"] },
                    ["*.roads"] = new AuthColumnRuleConfig { Allow = ["id", "kind"], Hide = ["secret"] }
                }
            }
        ]
    };
    var service = new AuthService(config, NullLogger<AuthService>.Instance);
    var rules = service.ResolveColumnRulesAsync(["reader"], "public", "roads").GetAwaiter().GetResult();
    Assert(rules.Allow.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["id", "name", "kind"]), "allow union");
    Assert(rules.Hide.SequenceEqual(["secret"]), "hide intersection");
}

static void AuthHideWins()
{
    var config = new PgDataHubConfig
    {
        Roles =
        [
            new AuthRoleConfig
            {
                Name = "reader",
                CollectionsAllow = ["public.*"],
                ColumnRules = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["public.*"] = new AuthColumnRuleConfig { Hide = ["secret"] },
                    ["*.roads"] = new AuthColumnRuleConfig { Hide = ["salary"] }
                }
            }
        ]
    };
    var service = new AuthService(config, NullLogger<AuthService>.Instance);
    var rules = service.ResolveColumnRulesAsync(["reader"], "public", "roads").GetAwaiter().GetResult();
    Assert(rules.Hide.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["secret", "salary"]),
        "a matching hide rule must not be undone by another match");
}

static void MetadataSqlShape()
{
    Assert(MetadataSql.Functions.Contains("p.oid", StringComparison.Ordinal), "function OID");
    Assert(MetadataSql.FunctionParameters.Contains("coalesce(p.proallargtypes, p.proargtypes::oid[])", StringComparison.Ordinal), "ordinary IN args");
    Assert(MetadataSql.Collections.Contains("SELECT DISTINCT ON (n.nspname, c.relname)", StringComparison.Ordinal), "one feature id");
    Assert(MetadataSql.Collections.Contains("LEFT JOIN LATERAL", StringComparison.Ordinal), "one geometry row");
}

static void ConfigWriterPreservesContent()
{
    var path = Path.Combine(Path.GetTempPath(), $"pgdatahub-{Guid.NewGuid():N}.ini");
    try
    {
        File.WriteAllText(path, "# keep me\n[custom.plugin]\nvalue=untouched\n\n[feature]\ndefaultLimit=1\nmaxLimit=2\n");
        var config = new PgDataHubConfig { SourcePath = path };
        config.Database.Database = "db";
        config.Database.User = "user";
        config.Feature.DefaultLimit = 3;
        config.Feature.MaxLimit = 4;
        IniAuthWriter.SaveAll(config);
        var content = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert(content.Contains("[custom.plugin]\nvalue=untouched", StringComparison.Ordinal), "unknown section");
        Assert(content.Contains("defaultLimit=3", StringComparison.Ordinal), "updated key");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*.tmp").Any(), "temporary file cleanup");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static Dictionary<string, string?> Query(params (string Key, string Value)[] values)
    => values.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.OrdinalIgnoreCase);

static CollectionMetadata Collection() => CollectionWith("id");

static CollectionMetadata CollectionWith(params string[] names) => new()
{
    Schema = "public",
    Name = "roads",
    Columns = names.Select(n => new ColumnMetadata { Name = n, Type = "text" }).ToList()
};

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
