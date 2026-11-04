using PgDataHub.Config;
using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class FeatureQueryBuilderTests
{
    [Fact]
    public void Quoted_identifiers_are_escaped_in_projection_filters_and_ordering()
    {
        var collection = TestData.Collection("a\"b");
        collection.Schema = "sch\"ema";
        collection.Name = "ta\"ble";
        var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection, new() {
            Filters = [new() { PropertyName = "a\"b", Operator = FilterOperator.Eq, Values = ["value"] }] });
        Assert.Contains("from \"sch\"\"ema\".\"ta\"\"ble\"", query.Sql);
        Assert.Contains("t.\"a\"\"b\" = @p0", query.Sql);
        Assert.Contains("order by t.\"a\"\"b\" asc", query.Sql);
    }

    [Fact]
    public void Pagination_uses_primary_key_and_breaks_explicit_order_ties()
    {
        var builder = new FeatureQueryBuilder(new PgDataHubConfig());
        var collection = TestData.Collection("id", "name");
        Assert.Contains("order by t.\"id\" asc", builder.Build(collection).Sql);
        Assert.Contains("order by t.\"name\" desc, t.\"id\" asc",
            builder.Build(collection, new() { OrderBy = ["-name"], Offset = 10 }).Sql);
        Assert.Contains("order by t.\"id\" desc\n", builder.Build(collection, new() { OrderBy = ["-id"] }).Sql.Replace("\r\n", "\n"));
        Assert.Contains("order by t.\"id\" asc", builder.Build(collection, new() { Select = ["name"] }).Sql);
    }

    [Fact]
    public void Distinct_projection_does_not_add_unselected_primary_key()
    {
        var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(TestData.Collection("id", "name"),
            new() { Distinct = true, Select = ["name"] });
        Assert.DoesNotContain("t.\"id\"", query.Sql);
    }

    [Fact]
    public void Hidden_or_missing_primary_key_is_not_used_for_ordering()
    {
        var collection = TestData.Collection("name");
        collection.PrimaryKey = null;
        Assert.DoesNotContain("order by", new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection).Sql);
    }

    [Fact]
    public void Build_caps_requested_limit_at_configured_maximum()
    {
        var config = new PgDataHubConfig { Feature = new FeatureConfig { DefaultLimit = 50, MaxLimit = 7 } };
        var query = new FeatureQueryBuilder(config).Build(
            TestData.Collection("id"), new FeatureQueryParameters { Limit = 99 });

        Assert.Contains("limit 7", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_uses_default_limit_when_request_omits_it()
    {
        var config = new PgDataHubConfig { Feature = new FeatureConfig { DefaultLimit = 12, MaxLimit = 50 } };
        var query = new FeatureQueryBuilder(config).Build(TestData.Collection("id"));
        Assert.Contains("limit 12", query.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("integer", "x")]
    [InlineData("numeric", "x")]
    [InlineData("boolean", "yes")]
    [InlineData("uuid", "x")]
    [InlineData("date", "not-a-date")]
    [InlineData("timestamp with time zone", "not-a-date")]
    public void Build_rejects_invalid_typed_filter_values(string type, string value)
    {
        var collection = TestData.Collection("id");
        collection.Columns[0].Type = type;
        var parameters = new FeatureQueryParameters
        {
            Filters = [new PropertyFilter { PropertyName = "id", Operator = FilterOperator.Eq, Values = [value] }]
        };

        Assert.Throws<ArgumentException>(() => new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection, parameters));
    }

    [Fact]
    public void Build_parameterizes_filter_values()
    {
        var collection = TestData.Collection("name");
        var parameters = new FeatureQueryParameters
        {
            Filters = [new PropertyFilter { PropertyName = "name", Operator = FilterOperator.Eq, Values = ["Robert'); drop table roads;--"] }]
        };

        var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection, parameters);

        Assert.DoesNotContain("drop table", query.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(query.Parameters);
        Assert.Equal("Robert'); drop table roads;--", query.Parameters[0].Value);
    }

    [Fact]
    public void Build_rejects_unknown_selected_column()
    {
        var parameters = new FeatureQueryParameters { Select = ["missing"] };
        Assert.Throws<InvalidOperationException>(() =>
            new FeatureQueryBuilder(new PgDataHubConfig()).Build(TestData.Collection("id"), parameters));
    }

    [Fact]
    public void Build_rejects_negative_offset()
    {
        var parameters = new FeatureQueryParameters { Offset = -1 };
        Assert.Throws<ArgumentException>(() =>
            new FeatureQueryBuilder(new PgDataHubConfig()).Build(TestData.Collection("id"), parameters));
    }

    [Fact]
    public void Build_converts_user_wildcard_to_parameterized_sql_wildcard()
    {
        var collection = TestData.Collection("name");
        var parameters = new FeatureQueryParameters
        {
            Filters = [new PropertyFilter { PropertyName = "name", Operator = FilterOperator.Like, Values = ["Ali*"] }]
        };

        var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection, parameters);

        Assert.Single(query.Parameters);
        Assert.Equal("Ali%", query.Parameters[0].Value);
    }
}
