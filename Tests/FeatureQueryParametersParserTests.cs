using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class FeatureQueryParametersParserTests
{
    [Theory]
    [InlineData("limit", "many")]
    [InlineData("limit", "1.5")]
    [InlineData("distinct", "yes")]
    [InlineData("count", "1")]
    [InlineData("bbox", "1,2,nope,4")]
    [InlineData("bbox", "4,3,1,2")]
    [InlineData("bbox", "1,2,3")]
    [InlineData("bbox-crs", "EPSG:nope")]
    [InlineData("datetime", "not-a-date")]
    [InlineData("datetime", "2026-01-01")]
    [InlineData("datetime", "2026-02-01T00:00:00Z/2026-01-01T00:00:00Z")]
    [InlineData("bbox", "-180,-100,180,90")]
    public void Parse_rejects_invalid_values(string key, string value)
        => Assert.Throws<ArgumentException>(() =>
            FeatureQueryParametersParser.Parse(TestData.Query((key, value))));

    [Fact]
    public void Parse_reads_paging_spatial_and_boolean_values()
    {
        var result = FeatureQueryParametersParser.Parse(TestData.Query(
            ("limit", "25"), ("offset", "10"), ("distinct", "true"), ("count", "false"),
            ("bbox", "28,40,30,42"), ("bbox-crs", "EPSG:4326"), ("crs", "3857")));

        Assert.Equal(25, result.Limit);
        Assert.Equal(10, result.Offset);
        Assert.True(result.Distinct);
        Assert.False(result.Count);
        Assert.NotNull(result.Bbox);
        Assert.Equal([28d, 40d, 30d, 42d], result.Bbox);
        Assert.Equal(4326, result.BboxCrsSrid);
        Assert.Equal(3857, result.ResponseCrsSrid);
    }

    [Fact]
    public void Parse_treats_star_select_as_all_columns()
    {
        var result = FeatureQueryParametersParser.Parse(TestData.Query(("select", "*")));
        Assert.Null(result.Select);
    }

    [Fact]
    public void Parse_turns_non_reserved_parameters_into_filters()
    {
        var result = FeatureQueryParametersParser.Parse(TestData.Query(("name", "Ali*")));
        var filter = Assert.Single(result.Filters);
        Assert.Equal("name", filter.PropertyName);
        Assert.Equal(FilterOperator.Like, filter.Operator);
        Assert.Equal(["Ali*"], filter.Values);
    }
}
