using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using PgDataHub.Config;
using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class JsonWriterTests
{
    [Fact]
    public async Task Distance_search_keeps_spatial_index_and_uses_a_single_materialized_operand()
    {
        InitializeDatabase();
        await using var connection = await Db.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand("""
            create temporary table spatial_fixture(id integer primary key, geom geometry(Point,3857));
            insert into spatial_fixture select v, ST_SetSRID(ST_MakePoint(v,v),3857) from generate_series(1,50000) v;
            create index spatial_fixture_geom_idx on spatial_fixture using gist(geom);
            analyze spatial_fixture;
            """, connection)) await setup.ExecuteNonQueryAsync();
        try
        {
            var collection = TestData.Collection("id", "geom");
            collection.Schema = "pg_temp";
            collection.Name = "spatial_fixture";
            collection.IsSpatial = true;
            collection.GeometryColumn = "geom";
            collection.Srid = 3857;
            using var geometry = JsonDocument.Parse("""{"type":"Point","coordinates":[0,0]}""");
            var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection, new() { Limit = 25 },
                includeNext: true, analysis: new("dwithin", new() { Geometry = geometry.RootElement, InputCrsSrid = 3857, Distance = 100 }));
            await using var explain = new NpgsqlCommand("explain (analyze, format json) " + query.Sql, connection);
            explain.Parameters.AddRange(query.Parameters.ToArray());
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            Assert.Contains("spatial_fixture_geom_idx", plan);
            Assert.Contains("analysis_input", plan);
            Assert.Equal(25, query.PageSize);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("drop table spatial_fixture", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Default_pagination_uses_primary_key_index_without_a_sort()
    {
        InitializeDatabase();
        await using var connection = await Db.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand("""
            create temporary table pagination_fixture(id integer primary key, name text);
            insert into pagination_fixture select v, 'value' from generate_series(1, 50000) v;
            analyze pagination_fixture;
            """, connection))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var collection = TestData.Collection("id", "name");
            collection.Schema = "pg_temp";
            collection.Name = "pagination_fixture";
            var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(collection, new() { Limit = 25 });
            await using var explain = new NpgsqlCommand("explain (format json) " + query.Sql, connection);
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            Assert.Contains("Index Scan", plan);
            Assert.DoesNotContain("\"Node Type\": \"Sort\"", plan);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("drop table pagination_fixture", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("geom", false, false)]
    [InlineData("shape", false, false)]
    [InlineData("geometry", false, false)]
    [InlineData("geom", true, false)]
    [InlineData("geom", false, true)]
    public async Task Spatial_column_name_nulls_and_empty_results_produce_geojson(string geometryColumn, bool nullGeometry, bool empty)
    {
        InitializeDatabase();
        var collection = TestData.Collection("id", geometryColumn);
        collection.IsSpatial = true;
        collection.GeometryColumn = geometryColumn;
        await using var connection = await Db.OpenConnectionAsync();
        var geometry = nullGeometry ? "null::jsonb" : "'{\"type\":\"Point\",\"coordinates\":[29,41]}'::jsonb";
        await using var command = new NpgsqlCommand($"select 1 as id, {geometry} as \"{geometryColumn}\" where {(empty ? "false" : "true")}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await using var result = new FeatureResult(collection, connection, command, reader);
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();
        await JsonWriter.WriteAsync(response, result);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);

        Assert.Equal("application/geo+json", response.ContentType);
        Assert.Equal("FeatureCollection", json.RootElement.GetProperty("type").GetString());
        var features = json.RootElement.GetProperty("features");
        Assert.Equal(empty ? 0 : 1, features.GetArrayLength());
        if (empty) return;
        Assert.Equal(1, features[0].GetProperty("properties").GetProperty("id").GetInt32());
        Assert.False(features[0].GetProperty("properties").TryGetProperty(geometryColumn, out _));
        Assert.Equal(nullGeometry ? JsonValueKind.Null : JsonValueKind.Object, features[0].GetProperty("geometry").ValueKind);
    }

    [Fact]
    public async Task Omitted_geometry_does_not_treat_a_property_named_geometry_as_spatial()
    {
        InitializeDatabase();
        var collection = TestData.Collection("id", "geom", "geometry");
        collection.IsSpatial = true;
        collection.GeometryColumn = "geom";
        await using var connection = await Db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("select 1 as id, 'ordinary property'::text as geometry", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await using var result = new FeatureResult(collection, connection, command, reader);
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();
        await JsonWriter.WriteAsync(response, result);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal("ordinary property", json.RootElement.GetProperty("items")[0].GetProperty("geometry").GetString());
    }

    internal static void InitializeDatabase()
        => Db.Initialize(PgDataHubConfig.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../pgdatahub.ini"))).Database);
}
