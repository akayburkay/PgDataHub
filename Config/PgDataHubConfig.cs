namespace PgDataHub.Config;

public sealed class PgDataHubConfig
{
    public static PgDataHubConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file not found: {path}");
        }

        var config = new PgDataHubConfig();

        string? section = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            var idx = line.IndexOf('=');

            if (idx < 0 || section is null)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            Parse(config, section, key, value);
        }

        return config;
    }

    private static void Parse(
        PgDataHubConfig config,
        string section,
        string key,
        string value)
    {
        switch (section.ToLowerInvariant())
        {
            case "database":
                ParseDatabase(config.Database, key, value);
                break;

            case "collections":
                ParseCollections(config.Collections, key, value);
                break;

            case "feature":
                ParseFeature(config.Feature, key, value);
                break;

            case "function":
                ParseFunction(config.Function, key, value);
                break;

            default:
                ParseCollectionOverride(config, section, key, value);
                break;
        }
    }

    private static void ParseDatabase(
        DatabaseConfig db,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "host":
                db.Host = value;
                break;

            case "port":
                if (int.TryParse(value, out var port))
                    db.Port = port;
                break;

            case "database":
                db.Database = value;
                break;

            case "user":
                db.User = value;
                break;

            case "password":
                db.Password = value;
                break;

            case "minpoolsize":
                if (int.TryParse(value, out var minPool))
                    db.MinPoolSize = minPool;
                break;

            case "maxpoolsize":
                if (int.TryParse(value, out var maxPool))
                    db.MaxPoolSize = maxPool;
                break;

            case "commandtimeout":
                if (int.TryParse(value, out var timeout))
                    db.CommandTimeout = timeout;
                break;
        }
    }

    private static void ParseCollections(
        CollectionsConfig collections,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
         

            case "include":
                collections.Include = Split(value);
                break;

            case "exclude":
                collections.Exclude = Split(value);
                break;
        }
    }

    private static void ParseFeature(
        FeatureConfig feature,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "defaultlimit":
                if (int.TryParse(value, out var defaultLimit))
                    feature.DefaultLimit = defaultLimit;
                break;

            case "maxlimit":
                if (int.TryParse(value, out var maxLimit))
                    feature.MaxLimit = maxLimit;
                break;

       
        }
    }

    private static void ParseFunction(
        FunctionConfig function,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "include":
                function.Include = Split(value);
                break;

            case "exclude":
                function.Exclude = Split(value);
                break;

            

            case "defaulttimeout":
                if (int.TryParse(value, out var timeout))
                    function.DefaultTimeout = timeout;
                break;

            case "maxrows":
                if (int.TryParse(value, out var maxRows))
                    function.MaxRows = maxRows;
                break;
        }
    }

    private static void ParseCollectionOverride(
        PgDataHubConfig config,
        string section,
        string key,
        string value)
    {
        if (!section.EndsWith(
                ".feature",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var table = section[..^8];

        if (!config.Tables.TryGetValue(
                table,
                out var collection))
        {
            collection = new CollectionConfig();
            config.Tables[table] = collection;
        }

        switch (key.ToLowerInvariant())
        {
            

            case "allow":
                collection.Feature.Allow = Split(value);
                break;

            case "hide":
                collection.Feature.Hide = Split(value);
                break;
        }
    }

    private static List<string> Split(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public DatabaseConfig Database { get; set; } = new();

    public CollectionsConfig Collections { get; set; } = new();

    public FeatureConfig Feature { get; set; } = new();

    public FunctionConfig Function { get; set; } = new();

    public Dictionary<string, CollectionConfig> Tables { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "";

    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    public int MinPoolSize { get; set; } = 5;

    public int MaxPoolSize { get; set; } = 100;

    public int CommandTimeout { get; set; } = 30;
}

public sealed class CollectionsConfig
{
    public List<string> Include { get; set; } = ["*"];

    public List<string> Exclude { get; set; } = [];
}

public sealed class FeatureConfig
{
    public int DefaultLimit { get; set; } = 1000;

    public int MaxLimit { get; set; } = 10000;

}

public sealed class FunctionConfig
{
    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];


    public int DefaultTimeout { get; set; } = 30;

    public int MaxRows { get; set; } = 10000;
}

public sealed class CollectionConfig
{
    public FeatureCollectionConfig Feature { get; set; } = new();
}

public sealed class FeatureCollectionConfig
{

    public List<string> Allow { get; set; } = [];

    public List<string> Hide { get; set; } = [];
}