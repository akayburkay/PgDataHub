using System.Diagnostics;
using Npgsql;
using PgDataHub.Config;
using PgDataHub.Features;
using PgDataHub.Metadata;

namespace PgDataHub.Tests;

// Run explicitly with --filter Category=Benchmark for reproducible measurements.
public sealed class CqlBenchmarkTests
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Isolated_postgis_query_benchmark()
    {
        JsonWriterTests.InitializeDatabase();
        await using var connection = await Db.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand("create temporary table cql_bench(id integer primary key, category integer, geom geometry(Point,4326)); create index cql_bench_category on cql_bench(category); create index cql_bench_geom on cql_bench using gist(geom)", connection))
            await setup.ExecuteNonQueryAsync();
        var collection = new CollectionMetadata { Schema = "pg_temp", Name = "cql_bench", IsSpatial = true, GeometryColumn = "geom", Srid = 4326, PrimaryKey = "id",
            Columns = [new() { Name = "id", Type = "integer" }, new() { Name = "category", Type = "integer" }, new() { Name = "geom", Type = "geometry" }] };
        var builder = new FeatureQueryBuilder(new PgDataHubConfig());
        async Task<int[]> Run(FeatureQueryParameters parameters)
        {
            var query = builder.Build(collection, parameters);
            await using var command = new NpgsqlCommand(query.Sql, connection);
            command.Parameters.AddRange(query.Parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<int>();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
            return ids.ToArray();
        }
        var cases = new (string Name, FeatureQueryParameters Parameters)[]
        {
            ("legacy", new() { Select = ["id"], Limit = 100, Filters = [new() { PropertyName = "category", Operator = FilterOperator.Eq, Values = ["7"] }] }),
            ("cql-text", new() { Select = ["id"], Limit = 100, Cql = CqlParser.ParseText("category = 7") }),
            ("cql-json", new() { Select = ["id"], Limit = 100, Cql = CqlParser.ParseJson("""{"op":"=","args":[{"property":"category"},7]}""") }),
            ("bbox", new() { Select = ["id"], Limit = 100, Bbox = [28, 40, 30, 42] }),
            ("s_intersects", new() { Select = ["id"], Limit = 100, Cql = CqlParser.ParseText("S_INTERSECTS(geom,BBOX(28,40,30,42))") }),
            ("s_within", new() { Select = ["id"], Limit = 100, Cql = CqlParser.ParseText("S_WITHIN(geom,BBOX(28,40,30,42))") }),
            ("s_contains", new() { Select = ["id"], Limit = 100, Cql = CqlParser.ParseText("S_CONTAINS(geom,POINT(28 40))") }),
            ("crs-transform", new() { Select = ["id"], Limit = 100, FilterCrsSrid = 3857, Cql = CqlParser.ParseText("S_INTERSECTS(geom,BBOX(3000000,4700000,3400000,5200000))") }),
            ("complex", new() { Select = ["id"], Limit = 100, Cql = CqlParser.ParseText("(category = 7 OR category = 8) AND id > 500") }),
            ("pagination", new() { Select = ["id"], Limit = 1000, Offset = 100, Cql = CqlParser.ParseText("category = 7") })
        };
        Console.WriteLine($"benchmark environment: {Environment.OSVersion}; .NET {Environment.Version}; CPUs={Environment.ProcessorCount}; RAM={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576}MiB");
        foreach (var size in new[] { 10_000, 100_000, 1_000_000 })
        {
            await using (var insert = new NpgsqlCommand("truncate cql_bench; insert into cql_bench select n, n % 10, ST_SetSRID(ST_MakePoint(27 + (n % 4000) * .001, 39 + (n % 4000) * .001),4326) from generate_series(1,@size) n; analyze cql_bench", connection))
            { insert.Parameters.AddWithValue("size", size); await insert.ExecuteNonQueryAsync(); }
            var legacy = await Run(cases[0].Parameters);
            Assert.Equal(legacy, await Run(cases[1].Parameters));
            Assert.Equal(legacy, await Run(cases[2].Parameters));
            Assert.Equal(await Run(cases[3].Parameters), await Run(cases[4].Parameters));
            foreach (var item in cases)
            {
                await Run(item.Parameters);
                var samples = new List<double>();
                for (var repeat = 0; repeat < 3; repeat++)
                    for (var n = 0; n < 10; n++)
                    {
                        var watch = Stopwatch.StartNew();
                        await Run(item.Parameters);
                        samples.Add(watch.Elapsed.TotalMilliseconds);
                    }
                samples.Sort();
                var avg = samples.Average();
                Console.WriteLine($"size={size} case={item.Name} avg={avg:F2}ms p50={samples[14]:F2}ms p95={samples[28]:F2}ms p99={samples[29]:F2}ms rps={1000 / avg:F1}");
            }
            foreach (var item in new[] { cases[0], cases[3], cases[4] })
            {
                var query = builder.Build(collection, item.Parameters);
                await using var explain = new NpgsqlCommand("explain (analyze, buffers, format text) " + query.Sql, connection);
                explain.Parameters.AddRange(query.Parameters.ToArray());
                await using var reader = await explain.ExecuteReaderAsync();
                var lines = new List<string>();
                while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
                Console.WriteLine($"EXPLAIN size={size} case={item.Name}: {string.Join(" | ", lines.Where(x => x.Contains("Scan") || x.Contains("Index Cond") || x.Contains("Execution Time") || x.Contains("Buffers:")))}");
            }
            await using (var selective = new NpgsqlCommand("explain (analyze, buffers, format text) select count(*) from cql_bench where ST_Intersects(geom, ST_MakeEnvelope(28.001,40.001,28.010,40.010,4326))", connection))
            {
                await using var reader = await selective.ExecuteReaderAsync();
                var lines = new List<string>();
                while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
                Console.WriteLine($"EXPLAIN size={size} selective-spatial-count: {string.Join(" | ", lines.Where(x => x.Contains("Scan") || x.Contains("Index Cond") || x.Contains("Execution Time") || x.Contains("Buffers:")))}");
            }
        }
    }
}
