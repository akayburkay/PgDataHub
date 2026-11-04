using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Features;
using PgDataHub.Metadata;

namespace PgDataHub.Tests;

public sealed class ApiRegressionTests
{
    [Fact]
    public async Task Browser_preflight_accepts_api_key_and_unauthorized_responses_keep_cors_headers()
    {
        await using var api = new TestApi();
        using var client = api.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/collections/public.roads/items");
        request.Headers.Add("Origin", "https://maps.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "X-API-Key");
        var preflight = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("x-api-key", preflight.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        client.DefaultRequestHeaders.Add("Origin", "https://maps.example");
        var denied = await client.GetAsync("/metadata");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("*", denied.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Configured_cors_origin_allows_post_and_rejects_other_origins()
    {
        await using var api = new TestApi(settings: new() { ["Cors:AllowedOrigins:0"] = "https://maps.example" });
        using var client = api.Factory.CreateClient();
        foreach (var origin in new[] { "https://maps.example", "https://other.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/functions/public.write/items");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            var response = await client.SendAsync(request);
            Assert.Equal(origin == "https://maps.example", response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task Login_limit_uses_forwarded_ip_only_from_a_trusted_proxy()
    {
        await using var api = new TestApi(settings: new() { ["ReverseProxy:KnownProxies:0"] = "10.10.0.2" });
        using var client = api.Factory.CreateClient();
        async Task<int> Login(string proxy, string address)
        {
            var context = await api.Factory.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(proxy);
                context.Request.Method = "POST";
                context.Request.Path = "/admin/session";
                context.Request.Headers["X-Forwarded-For"] = address;
                context.Request.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes("{\"apiKey\":\"invalid-test-key\"}");
                context.Request.ContentLength = body.Length;
                context.Request.Body = new MemoryStream(body);
            });
            return context.Response.StatusCode;
        }
        for (var i = 0; i < 5; i++) Assert.Equal(401, await Login("10.10.0.2", "192.0.2.1"));
        Assert.Equal(429, await Login("10.10.0.2", "192.0.2.1"));
        Assert.Equal(401, await Login("10.10.0.2", "192.0.2.2"));
        for (var i = 0; i < 5; i++) Assert.Equal(401, await Login("10.10.0.3", $"198.51.100.{i + 1}"));
        Assert.Equal(429, await Login("10.10.0.3", "198.51.100.99"));
    }

    [Fact]
    public async Task Volatile_functions_require_opt_in_post_and_separate_permission_for_names_and_oids()
    {
        JsonWriterTests.InitializeDatabase();
        var schema = "pgdh_test_" + Guid.NewGuid().ToString("N");
        await using var connection = await Db.OpenConnectionAsync();
        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            await Execute($"""
                create schema {schema};
                create table {schema}.writes (id integer);
                create function {schema}.write_value(v integer) returns integer language plpgsql volatile as $$
                begin insert into {schema}.writes values (v); return v; end $$;
                create function {schema}.read_value() returns integer language sql stable as $$ select 7 $$;
                create function {schema}.constant_value() returns integer language sql immutable as $$ select 8 $$;
                """);
            await using var api = new TestApi(c => c.Function.Include = [schema + ".*"]);
            using var client = api.Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", api.Key);
            var runtime = api.Factory.Services.GetRequiredService<PgDataHubConfig>();
            var metadata = api.Factory.Services.GetRequiredService<MetadataService>();
            var functions = await metadata.GetFunctionsAsync();
            Assert.Equal(2, functions.Count);
            Assert.All(functions, f => Assert.False(f.IsVolatile));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/functions/{schema}.write_value/items?v=1")).StatusCode);
            runtime.Function.VolatileInclude = [schema + ".write_value"];
            await metadata.RefreshAsync();
            var function = (await metadata.GetFunctionsAsync()).Single(f => f.IsVolatile);
            foreach (var id in new[] { $"{schema}.write_value", function.Oid.ToString() })
            {
                var url = $"/functions/{id}/items?v=42";
                var get = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
                Assert.Contains("POST", get.Content.Headers.Allow);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(url, null)).StatusCode);
            }
            runtime.Roles.Single().FunctionsVolatileAllow = [schema + ".*"];
            await api.Factory.Services.GetRequiredService<AuthService>().RefreshAsync();
            foreach (var id in new[] { $"{schema}.write_value", function.Oid.ToString() })
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/functions/{id}/items?v=42", null)).StatusCode);
            await using var count = new NpgsqlCommand($"select count(*) from {schema}.writes", connection);
            Assert.Equal(2L, await count.ExecuteScalarAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/functions/{schema}.read_value/items")).StatusCode);

            // Even stale cached metadata must not let a GET modify data.
            await Execute($"""
                create or replace function {schema}.read_value() returns integer language plpgsql volatile as $$
                begin insert into {schema}.writes values (99); return 99; end $$;
                """);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/functions/{schema}.read_value/items")).StatusCode);
            Assert.Equal(2L, await count.ExecuteScalarAsync());
            runtime.Auth.Enabled = false;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/functions/{function.Oid}/items?v=42", null)).StatusCode);
        }
        finally { await Execute($"drop schema if exists {schema} cascade"); }
    }

    internal sealed class TestApi : IAsyncDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("PGDATAHUB_CONFIG_PATH");
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"pgdh-regression-{Guid.NewGuid():N}.ini");
        public string Key { get; } = AuthService.GenerateRawKey();
        public WebApplicationFactory<FeatureController> Factory { get; }

        public TestApi(Action<PgDataHubConfig>? configure = null, Dictionary<string, string?>? settings = null)
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../pgdatahub.ini"));
            File.Copy(source, _path);
            var config = PgDataHubConfig.Load(_path);
            config.Auth.Enabled = true;
            config.Auth.DefaultRole = "";
            config.Auth.ApiKey.Enabled = true;
            config.Roles = [new() { Name = "reader", FunctionsAllow = ["*"] }];
            config.ApiKeys = [new() { Id = 1, Name = "test", Hash = AuthService.HashKey(Key), Role = "reader" }];
            configure?.Invoke(config);
            IniAuthWriter.SaveAll(config);
            Environment.SetEnvironmentVariable("PGDATAHUB_CONFIG_PATH", _path);
            Factory = new WebApplicationFactory<FeatureController>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings ?? []));
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("PGDATAHUB_CONFIG_PATH", _previous);
            File.Delete(_path);
        }
    }
}
