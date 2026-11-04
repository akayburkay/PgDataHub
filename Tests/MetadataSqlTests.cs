using PgDataHub.Metadata;

namespace PgDataHub.Tests;

public sealed class MetadataSqlTests
{
    [Fact]
    public void Function_metadata_query_includes_oid_and_signature_data()
    {
        Assert.Contains("p.oid", MetadataSql.Functions, StringComparison.Ordinal);
        Assert.Contains("pg_get_function_identity_arguments", MetadataSql.Functions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Function_parameter_query_supports_ordinary_in_arguments()
        => Assert.Contains("coalesce(p.proallargtypes, p.proargtypes::oid[])",
            MetadataSql.FunctionParameters, StringComparison.Ordinal);

    [Fact]
    public void Collection_query_prevents_duplicate_rows_from_indexes_and_geometry_columns()
    {
        Assert.Contains("SELECT DISTINCT ON (n.nspname, c.relname)", MetadataSql.Collections, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN LATERAL", MetadataSql.Collections, StringComparison.Ordinal);
    }
}
