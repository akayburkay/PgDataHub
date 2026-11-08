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
    public async Task Untyped_geometry_uses_stored_srid_for_bbox_and_geojson()
    {
        JsonWriterTests.InitializeDatabase();
        var schema = "pgdh_srid_" + Guid.NewGuid().ToString("N");
        await using var connection = await PgDataHub.Config.Db.OpenConnectionAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"""
                create schema {schema};
                create table {schema}.features(id integer primary key, geom geometry);
                insert into {schema}.features values (1, ST_SetSRID(ST_Point(30, 40), 4326));
                """, connection))
                await create.ExecuteNonQueryAsync();

            await using var api = new ApiRegressionTests.TestApi(c =>
                c.Roles.Single().CollectionsAllow = [schema + ".features"]);
            using var client = api.Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", api.Key);
            using var response = await client.GetAsync($"/collections/{schema}.features/items?bbox=25,35,35,45");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("numberReturned").GetInt32());
            Assert.Equal(30, json.RootElement.GetProperty("features")[0]
                .GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"drop schema if exists {schema} cascade", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Cql_and_legacy_filter_smoke_performance()
    {
        await using var fixture = await Fixture.Create();
        using var client = fixture.Client();
        async Task<long> Measure(string query)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 40; i++)
            {
                using var response = await client.GetAsync($"/collections/{fixture.Id}/items?{query}");
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
            return watch.ElapsedMilliseconds;
        }
        await Measure("id=1"); // warm-up
        var legacyMs = await Measure("id=1");
        var cqlMs = await Measure("filter=id%20%3D%201");
        Console.WriteLine($"40 real PostGIS requests: legacy={legacyMs}ms, CQL2={cqlMs}ms");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Concurrent_cql_http_smoke_performance()
    {
        await using var fixture = await Fixture.Create();
        using var client = fixture.Client();
        await client.GetAsync($"/collections/{fixture.Id}/items?filter=id%20%3E%200");
        foreach (var concurrency in new[] { 10, 50, 100 })
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using var response = await client.GetAsync($"/collections/{fixture.Id}/items?filter=id%20%3E%200");
                return (Ms: watch.Elapsed.TotalMilliseconds, Success: response.IsSuccessStatusCode);
            }));
            var sorted = results.Select(r => r.Ms).Order().ToArray();
            Assert.All(results, r => Assert.True(r.Success));
            Console.WriteLine($"HTTP concurrent={concurrency} avg={sorted.Average():F1}ms p50={sorted[(int)(sorted.Length * .5)]:F1}ms p95={sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .95))]:F1}ms p99={sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .99))]:F1}ms rps={concurrency / elapsed.Elapsed.TotalSeconds:F1} errors={results.Count(r => !r.Success)}");
        }
    }
    [Fact]
    public async Task Cql_text_json_spatial_and_row_policy_work_on_postgis()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Sql($"alter table {fixture.Id} add observed_at date; update {fixture.Id} set observed_at = '2026-01-01' where id = 1; update {fixture.Id} set observed_at = '2026-02-01' where id = 2;");
        using var client = fixture.Client();
        async Task<int> Count(string filter, string extra = "")
        {
            using var response = await client.GetAsync($"/collections/{fixture.Id}/items?count=true&filter={Uri.EscapeDataString(filter)}{extra}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("numberMatched").GetInt32();
        }
        Assert.Equal(1, await Count("S_INTERSECTS(geom,POINT(5 5))", "&filter-crs=EPSG:3857"));
        Assert.Equal(1, await Count("""{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[5,5]}]}""", "&filter-lang=cql2-json&filter-crs=EPSG:3857"));
        Assert.Equal(1, await Count("id = 1 OR id = 3")); // id=3 is hidden by row policy
        Assert.Equal(1, await Count("T_AFTER(observed_at,DATE('2026-01-15'))"));
        Assert.Equal(1, await Count("id + 1 = 3"));
        Assert.Equal(1, await Count("id > 0", "&label=Alpha*&bbox=19,19,31,31&bbox-crs=EPSG:3857"));
        using var hidden = await client.GetAsync($"/collections/{fixture.Id}/items?filter=tenant_id%20%3D%202");
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
        using var malformed = await client.GetAsync($"/collections/{fixture.Id}/items?filter-lang=cql2-json&filter={Uri.EscapeDataString("""{"op":"s_intersects","args":[{"property":"geom"},{"bbox":["bad",1,2,3]}]}""")}");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var badCrs = await client.GetAsync($"/collections/{fixture.Id}/items?filter=S_INTERSECTS(geom,POINT(5%205))&filter-crs=EPSG:999999");
        Assert.Equal(HttpStatusCode.BadRequest, badCrs.StatusCode);
        using var badWkt = await client.GetAsync($"/collections/{fixture.Id}/items?filter=S_INTERSECTS(geom,POINT(5))");
        Assert.Equal(HttpStatusCode.BadRequest, badWkt.StatusCode);
        using var badGeoJson = await client.GetAsync($"/collections/{fixture.Id}/items?filter-lang=cql2-json&filter={Uri.EscapeDataString("""{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[5]}]}""")}");
        Assert.Equal(HttpStatusCode.BadRequest, badGeoJson.StatusCode);
    }
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
