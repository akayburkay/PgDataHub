using PgDataHub.Metadata;

namespace PgDataHub.Tests;

internal static class TestData
{
    internal static Dictionary<string, string?> Query(params (string Key, string Value)[] values)
        => values.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.OrdinalIgnoreCase);

    internal static CollectionMetadata Collection(params string[] columns)
        => new()
        {
            Schema = "public",
            Name = "roads",
            PrimaryKey = columns.FirstOrDefault() ?? "id",
            Columns = (columns.Length == 0 ? ["id"] : columns)
                .Select(name => new ColumnMetadata { Name = name, Type = "text" })
                .ToList()
        };
}
