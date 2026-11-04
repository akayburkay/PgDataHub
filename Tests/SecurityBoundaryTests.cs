using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http;
using PgDataHub.Features;
using PgDataHub.Config;

namespace PgDataHub.Tests;

public sealed class SecurityBoundaryTests
{
    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; SELECT pg_sleep(10); --")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("\" onmouseover=alert(1) x=\"")]
    public void Untrusted_values_remain_parameters(string value)
    {
        var query = new FeatureQueryBuilder(new PgDataHubConfig()).Build(TestData.Collection("name"),
            new FeatureQueryParameters { Filters = [new PropertyFilter { PropertyName = "name", Operator = FilterOperator.Eq, Values = [value] }] });
        Assert.DoesNotContain(value, query.Sql);
        Assert.Equal(value, Assert.Single(query.Parameters).Value);
    }

    [Fact]
    public async Task Saturated_server_returns_429_and_recovers()
    {
        var previous = Environment.GetEnvironmentVariable("PGDATAHUB_CONFIG_PATH");
        Environment.SetEnvironmentVariable("PGDATAHUB_CONFIG_PATH", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../pgdatahub.ini")));
        try
        {
        await using var factory = new WebApplicationFactory<FeatureController>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Limits:ConcurrentRequests"] = "1" }));
        });
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        using var lease = await limiter.AcquireAsync(new DefaultHttpContext());
        Assert.True(lease.IsAcquired);
        try
        {
            var response = await client.GetAsync("/health/live");
            for (var attempt = 0; attempt < 50 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests; attempt++)
            {
                response.Dispose();
                await Task.Delay(20);
                response = await client.GetAsync("/health/live");
            }
            Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        finally { lease.Dispose(); }
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("PGDATAHUB_CONFIG_PATH", previous); }
    }

    private sealed class BlockingContent(TaskCompletionSource entered, TaskCompletionSource release) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            entered.TrySetResult();
            await release.Task;
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{}"));
        }
    }
}
