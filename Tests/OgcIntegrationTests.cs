using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class OgcIntegrationTests
{
    [Fact]
    public async Task Temporal_intervals_and_null_geometries_follow_core_matching_rules()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Sql($"""
            alter table {fixture.Id} add observed_at timestamp with time zone;
            update {fixture.Id} set observed_at = '2026-01-01T00:00:00Z' where id = 1;
            update {fixture.Id} set observed_at = '2026-02-01T00:00:00Z' where id = 2;
            """);
        using var client = fixture.Client();
        using var february = JsonDocument.Parse(await client.GetStringAsync($"/collections/{fixture.Id}/items?datetime=2026-02-01T00:00:00Z/.."));
        Assert.Equal(2, february.RootElement.GetProperty("features")[0].GetProperty("id").GetInt32());
        using var january = JsonDocument.Parse(await client.GetStringAsync($"/collections/{fixture.Id}/items?datetime=2026-01-01T00:00:00Z"));
        Assert.Equal(1, january.RootElement.GetProperty("features")[0].GetProperty("id").GetInt32());
        await fixture.Sql($"update {fixture.Id} set geom = null, observed_at = null where id = 2");
        using var nulls = JsonDocument.Parse(await client.GetStringAsync($"/collections/{fixture.Id}/items?id=2&datetime=2020-01-01T00:00:00Z&bbox=0,0,10,10&bbox-crs=3857"));
        Assert.Equal(1, nulls.RootElement.GetProperty("numberReturned").GetInt32());
        Assert.Equal(JsonValueKind.Null, nulls.RootElement.GetProperty("features")[0].GetProperty("geometry").ValueKind);
    }

    [Fact]
    public async Task Bbox_supports_antimeridian_six_coordinates_and_exact_three_dimensional_intersection()
    {
        await using var fixture = await Fixture.Create();
        var id = fixture.Schema + ".three_d";
        await fixture.Sql($"""
            create table {id}(id integer primary key, geom geometry(GeometryZ,4326));
            insert into {id} values (1, ST_GeomFromText('POINT Z(179 0 5)',4326)),
              (2, ST_GeomFromText('POINT Z(-179 0 25)',4326)), (3,null),
              (4,ST_GeomFromText('POINT Z(5 5 5)',4326)),
              (5,ST_GeomFromText('LINESTRING Z(1 1 100,80 80 1)',4326));
            """);
        using var client = fixture.Client();
        using var wrapped = JsonDocument.Parse(await client.GetStringAsync($"/collections/{id}/items?bbox=170,-10,-170,10&count=true"));
        Assert.Equal(3, wrapped.RootElement.GetProperty("numberMatched").GetInt32());
        using var height = await client.GetAsync($"/collections/{id}/items?bbox=170,-10,0,-170,10,10&count=true");
        Assert.True(height.IsSuccessStatusCode, await height.Content.ReadAsStringAsync());
        Assert.Equal("<" + OgcSupport.Crs84h + ">", height.Headers.GetValues("Content-Crs").Single());
        using var heightPage = JsonDocument.Parse(await height.Content.ReadAsStringAsync());
        Assert.Equal(2, heightPage.RootElement.GetProperty("numberMatched").GetInt32());
        using var volume = JsonDocument.Parse(await client.GetStringAsync($"/collections/{id}/items?bbox=0,0,0,10,10,10&count=true"));
        Assert.Equal(2, volume.RootElement.GetProperty("numberMatched").GetInt32());
    }

    [Fact]
    public async Task Discovery_pages_and_single_features_follow_ogc_links_and_authorization()
    {
        await using var fixture = await Fixture.Create();
        using var client = fixture.Api.Factory.CreateClient();
        using var landing = JsonDocument.Parse(await client.GetStringAsync("/"));
        Assert.Contains(landing.RootElement.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "data");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/conformance")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/collections")).StatusCode);
        client.DefaultRequestHeaders.Add("X-API-Key", fixture.Api.Key);
        using var collections = JsonDocument.Parse(await client.GetStringAsync("/collections?f=json"));
        var collection = Assert.Single(collections.RootElement.GetProperty("collections").EnumerateArray());
        Assert.Equal(fixture.Id, collection.GetProperty("id").GetString());
        var href = collection.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "items").GetProperty("href").GetString();
        using var first = await client.GetAsync(href + "?limit=100&f=geojson&count=true&bbox=-1,-1,40,40&bbox-crs=3857&label=Alpha*");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/geo+json", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal("<" + OgcSupport.Crs84 + ">", first.Headers.GetValues("Content-Crs").Single());
        using var page = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(1, page.RootElement.GetProperty("numberReturned").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("numberMatched").GetInt32());
        Assert.Equal(1, page.RootElement.GetProperty("features")[0].GetProperty("id").GetInt32());
        var next = page.RootElement.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "next").GetProperty("href").GetString()!;
        Assert.Contains("limit=1", next);
        Assert.Contains("label=Alpha", next);
        Assert.Contains("bbox-crs=3857", next);
        using var second = JsonDocument.Parse(await client.GetStringAsync(next));
        Assert.Equal(2, second.RootElement.GetProperty("features")[0].GetProperty("id").GetInt32());
        Assert.DoesNotContain(second.RootElement.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "next");
        using var feature = JsonDocument.Parse(await client.GetStringAsync($"/collections/{fixture.Id}/items/2?f=json&crs=3857"));
        Assert.Equal("Feature", feature.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, feature.RootElement.GetProperty("id").GetInt32());
        Assert.False(feature.RootElement.TryGetProperty("features", out _));
        Assert.False(feature.RootElement.GetProperty("properties").TryGetProperty("tenant_id", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/collections/{fixture.Schema}.secret")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/collections/{fixture.Id}/items/3")).StatusCode);
    }

    [Fact]
    public async Task Row_rules_cover_counts_projections_missing_claims_and_single_item_paths()
    {
        await using var fixture = await Fixture.Create();
        using var client = fixture.Client();
        foreach (var suffix in new[] { "?id=3&count=true", "?select=label&offset=2", "?bbox=-1,-1,11,11&bbox-crs=3857&id=3" })
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync($"/collections/{fixture.Id}/items" + suffix));
            Assert.Equal(0, json.RootElement.GetProperty("numberReturned").GetInt32());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/collections/{fixture.Id}/items?tenant_id=2")).StatusCode);
        var runtime = fixture.Api.Factory.Services.GetRequiredService<PgDataHubConfig>();
        runtime.Roles.Single().RowRules = [new() { Pattern = fixture.Id,
            Conditions = [new() { Column = "tenant_id", Claim = "tenant_id" }] }];
        await fixture.Api.Factory.Services.GetRequiredService<AuthService>().RefreshAsync();
        using var empty = JsonDocument.Parse(await client.GetStringAsync($"/collections/{fixture.Id}/items?count=true"));
        Assert.Equal(0, empty.RootElement.GetProperty("numberReturned").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/collections/{fixture.Id}/items/1")).StatusCode);
    }

    [Fact]
    public async Task Fifteen_analysis_operations_use_authorized_rows_and_native_unit_measurements()
    {
        await using var fixture = await Fixture.Create();
        using var client = fixture.Client();
        var geometry = JsonSerializer.Deserialize<JsonElement>("""{"type":"Polygon","coordinates":[[[2,2],[8,2],[8,8],[2,8],[2,2]]]}""");
        Assert.Equal(15, SpatialAnalysis.Operations.Count);
        foreach (var operation in SpatialAnalysis.Operations)
        {
            var body = new SpatialAnalysisRequest { Geometry = geometry, InputCrsSrid = 3857, WorkingCrsSrid = 3857,
                TargetCrsSrid = 3857, Distance = 1, Tolerance = 0.1 };
            var response = await client.PostAsJsonAsync($"/collections/{fixture.Id}/analysis/{operation.Name}?limit=1&orderby=id", body);
            Assert.True(response.IsSuccessStatusCode, operation.Name + ": " + await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var feature = Assert.Single(json.RootElement.GetProperty("features").EnumerateArray());
            Assert.Equal(1, feature.GetProperty("id").GetInt32());
            Assert.False(feature.GetProperty("properties").TryGetProperty("tenant_id", out _));
            if (operation.Name == "area") Assert.Equal(100, feature.GetProperty("properties").GetProperty("analysisValue").GetDouble(), 5);
            if (operation.Name == "distance") Assert.Equal(0, feature.GetProperty("properties").GetProperty("analysisValue").GetDouble());
            if (operation.Name is "intersects" or "dwithin") Assert.True(feature.GetProperty("properties").GetProperty("analysisValue").GetBoolean());
        }
        var deniedRow = await client.PostAsJsonAsync($"/collections/{fixture.Id}/analysis/area?id=3", new { });
        using var denied = JsonDocument.Parse(await deniedRow.Content.ReadAsStringAsync());
        Assert.Empty(denied.RootElement.GetProperty("features").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/collections/{fixture.Id}/analysis/distance", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/collections/{fixture.Id}/analysis/buffer", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/collections/{fixture.Id}/analysis/unknown", new { })).StatusCode);
    }

    [Fact]
    public async Task Dynamic_openapi_is_available_in_production_and_hides_forbidden_collections_and_columns()
    {
        await using var fixture = await Fixture.Create();
        using var anonymous = fixture.Api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/swagger/index.html")).StatusCode);
        using var client = fixture.Client();
        var response = await client.GetAsync("/api");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.0", document.RootElement.GetProperty("openapi").GetString());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty($"/collections/{fixture.Id}/items", out _));
        Assert.False(paths.TryGetProperty($"/collections/{fixture.Schema}.secret/items", out _));
        Assert.True(paths.TryGetProperty($"/collections/{fixture.Id}/analysis/dwithin", out _));
        Assert.DoesNotContain("tenant_id", await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("SpatialAnalysisRequest", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    [Fact]
    public async Task Format_negotiation_rejects_unknown_formats_and_unsupported_accepts()
    {
        await using var fixture = await Fixture.Create();
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.GetAsync($"/collections/{fixture.Id}/items?f=xml")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/collections/{fixture.Id}/items");
        request.Headers.Accept.ParseAdd("text/html");
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.SendAsync(request)).StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Schema { get; } = "pgdh_ogc_" + Guid.NewGuid().ToString("N");
        public string Id => Schema + ".features";
        public ApiRegressionTests.TestApi Api { get; private set; } = null!;
        private NpgsqlConnection _connection = null!;
        public static async Task<Fixture> Create()
        {
            JsonWriterTests.InitializeDatabase();
            var fixture = new Fixture { _connection = await Db.OpenConnectionAsync() };
            await using var command = new NpgsqlCommand($"""
                create schema {fixture.Schema};
                create table {fixture.Schema}.features(id integer primary key, tenant_id integer not null, label text, geom geometry(Polygon,3857));
                insert into {fixture.Schema}.features values
                (1,1,'Alpha one',ST_GeomFromText('POLYGON((0 0,10 0,10 10,0 10,0 0))',3857)),
                (2,1,'Alpha two',ST_GeomFromText('POLYGON((20 20,30 20,30 30,20 30,20 20))',3857)),
                (3,2,'Private',ST_GeomFromText('POLYGON((0 0,10 0,10 10,0 10,0 0))',3857));
                create table {fixture.Schema}.secret(id integer primary key);
                """, fixture._connection);
            await command.ExecuteNonQueryAsync();
            fixture.Api = new(c =>
            {
                c.Feature.DefaultLimit = 1;
                c.Feature.MaxLimit = 1;
                c.Roles.Single().CollectionsAllow = [fixture.Schema + ".*"];
                c.Roles.Single().CollectionsDeny = [fixture.Schema + ".secret"];
                c.Roles.Single().ColumnRules[fixture.Id] = new() { Hide = ["tenant_id"] };
                c.Roles.Single().RowRules = [new() { Pattern = fixture.Id,
                    Conditions = [new() { Column = "tenant_id", Values = ["1"] }] }];
            });
            return fixture;
        }
        public HttpClient Client()
        {
            var client = Api.Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", Api.Key);
            return client;
        }
        public async Task Sql(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            await using var cleanup = new NpgsqlCommand($"drop schema if exists {Schema} cascade", _connection);
            await cleanup.ExecuteNonQueryAsync();
            await _connection.DisposeAsync();
        }
    }
}
