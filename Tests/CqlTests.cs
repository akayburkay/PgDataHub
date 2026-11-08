using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class CqlTests
{
    [Fact]
    public void Text_and_json_use_same_sql_shape()
    {
        const string text = "age > 5 AND (name = 'O''Brien' OR name IS NULL)";
        const string json = """{"op":"and","args":[{"op":">","args":[{"property":"age"},5]},{"op":"or","args":[{"op":"=","args":[{"property":"name"},"O'Brien"]},{"op":"is null","args":[{"property":"name"}]}]}]}""";
        var collection = TestData.Collection("age", "name");
        collection.Columns[0].Type = "integer";
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var a = builder.Build(collection, FeatureQueryParametersParser.Parse(TestData.Query(("filter", text))));
        var b = builder.Build(collection, FeatureQueryParametersParser.Parse(TestData.Query(("filter", json), ("filter-lang", "cql2-json"))));
        Assert.Equal(a.Sql, b.Sql);
        Assert.Equal(2, a.Parameters.Count);
        Assert.Equal(5L, a.Parameters[0].Value);
        Assert.DoesNotContain("O'Brien", a.Sql);
    }

    [Fact]
    public void Cql_cannot_reference_hidden_columns_or_inject_sql()
    {
        var collection = TestData.Collection("public_name");
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        Assert.Throws<ArgumentException>(() => builder.Build(collection, new() { Cql = CqlParser.ParseText("secret = 'x'") }));
        Assert.Throws<ArgumentException>(() => CqlParser.ParseText("public_name = 'x'; DROP TABLE roads"));
        Assert.Throws<ArgumentException>(() => CqlParser.ParseText("public_name = 'x' OR secret = 'y' --"));
    }

    [Fact]
    public void Legacy_and_cql_filters_are_conjoined()
    {
        var collection = TestData.Collection("status");
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var query = builder.Build(collection, FeatureQueryParametersParser.Parse(TestData.Query(("status", "active"), ("filter", "status <> 'deleted'"))));
        Assert.Contains("t.\"status\" = @p0 and (t.\"status\" <> @cql1)", query.Sql);
    }

    [Fact]
    public void Spatial_text_and_json_share_compiler_and_transform_literal()
    {
        var collection = TestData.Collection("geom");
        collection.IsSpatial = true;
        collection.GeometryColumn = "geom";
        collection.Srid = 3857;
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var text = builder.Build(collection, new() { Cql = CqlParser.ParseText("S_INTERSECTS(geom,POINT(29 41))") });
        var json = builder.Build(collection, new() { Cql = CqlParser.ParseJson("""{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[29,41]}]}""") });
        Assert.Contains("ST_Transform", text.Sql);
        Assert.Contains("ST_Intersects", json.Sql);
        Assert.DoesNotContain("POINT(29", text.Sql);
        Assert.Single(text.Parameters);
        Assert.Single(json.Parameters);
    }

    [Fact]
    public void Arithmetic_and_temporal_literals_compile()
    {
        var collection = TestData.Collection("height", "clearance", "observed_at");
        collection.Columns[0].Type = "integer";
        collection.Columns[1].Type = "integer";
        collection.Columns[2].Type = "timestamp with time zone";
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var query = builder.Build(collection, new() { Cql = CqlParser.ParseText("height > clearance - 1 AND T_AFTER(observed_at,TIMESTAMP('2026-01-01T00:00:00Z'))") });
        Assert.Contains("t.\"clearance\" - @cql", query.Sql);
        Assert.Contains("t.\"observed_at\" > @cql", query.Sql);
    }

    [Fact]
    public void Arithmetic_text_and_json_have_equivalent_ast_and_sql()
    {
        var collection = TestData.Collection("height", "clearance");
        foreach (var col in collection.Columns) col.Type = "integer";
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var text = builder.Build(collection, new() { Cql = CqlParser.ParseText("height > clearance - 1") });
        var json = builder.Build(collection, new() { Cql = CqlParser.ParseJson("""{"op":">","args":[{"property":"height"},{"op":"-","args":[{"property":"clearance"},1]}]}""") });
        Assert.Equal(text.Sql, json.Sql);
        Assert.Equal(text.Parameters[0].Value, json.Parameters[0].Value);
    }

    [Fact]
    public void Json_isNull_matches_text_and_malformed_bbox_is_rejected()
    {
        var collection = TestData.Collection("label");
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var text = builder.Build(collection, new() { Cql = CqlParser.ParseText("label IS NULL") });
        var json = builder.Build(collection, new() { Cql = CqlParser.ParseJson("""{"op":"isNull","args":[{"property":"label"}]}""") });
        Assert.Equal(text.Sql, json.Sql);
        Assert.Throws<ArgumentException>(() => CqlParser.ParseJson("""{"op":"s_intersects","args":[{"property":"geom"},{"bbox":["bad",1,2,3]}]}"""));
    }

    [Fact]
    public void Negative_spatial_coordinates_parse_in_text_and_json()
    {
        var collection = TestData.Collection("geom");
        collection.IsSpatial = true; collection.GeometryColumn = "geom"; collection.Srid = 4326;
        var builder = new FeatureQueryBuilder(new PgDataHub.Config.PgDataHubConfig());
        var wkt = builder.Build(collection, new() { Cql = CqlParser.ParseText("S_INTERSECTS(geom,POINT(-29 -41))") });
        Assert.Contains("-29", wkt.Parameters[0].Value!.ToString());
        var bbox = builder.Build(collection, new() { Cql = CqlParser.ParseText("S_INTERSECTS(geom,BBOX(-30,-42,-28,-40))") });
        Assert.Equal(-30d, bbox.Parameters[0].Value);
    }
}
