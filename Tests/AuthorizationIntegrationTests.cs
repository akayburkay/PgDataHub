using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PgDataHub.Config;
using PgDataHub.Auth;
using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class AuthorizationIntegrationTests
{
    [Fact]
    public async Task Real_pipeline_rejects_bad_tokens_and_enforces_three_collection_role()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../pgdatahub.ini"));
        var path = Path.Combine(Path.GetTempPath(), $"pgdh-auth-{Guid.NewGuid():N}.ini");
        var old = Environment.GetEnvironmentVariable("PGDATAHUB_CONFIG_PATH");
        using var rsa = RSA.Create(2048);
        using var wrongRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        const string issuer = "https://issuer.test", audience = "pgdh-test";
        try
        {
            File.Copy(source, path);
            var config = PgDataHubConfig.Load(path);
            config.Auth.Enabled = true;
            config.Auth.DefaultRole = "";
            config.Auth.ApiKey.Enabled = true;
            config.Auth.Jwt.Enabled = true;
            config.Auth.Jwt.Authority = issuer;
            config.Auth.Jwt.Audience = audience;
            config.Auth.Jwt.RoleClaim = "roles";
            var adminRaw = AuthService.GenerateRawKey();
            var readerRaw = AuthService.GenerateRawKey();
            config.Roles = [new() { Name = "audit_admin", CollectionsAllow = ["*"] }, new() { Name = "audit_reader" }];
            config.ApiKeys = [new() { Id = 1, Name = "audit", Hash = AuthService.HashKey(adminRaw), Role = "audit_admin", Enabled = true }];
            IniAuthWriter.SaveAll(config);
            Environment.SetEnvironmentVariable("PGDATAHUB_CONFIG_PATH", path);
            await using var factory = new WebApplicationFactory<FeatureController>().WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.ConfigurationManager = null;
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = issuer };
                    options.Configuration.SigningKeys.Add(key);
                    options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                })));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", adminRaw);
            var metadata = await client.GetAsync("/metadata");
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            using var json = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
            var ids = json.RootElement.GetProperty("collections").EnumerateArray()
                .Select(c => c.GetProperty("schema").GetString() + "." + c.GetProperty("name").GetString()).ToArray();
            Assert.True(ids.Length >= 4, "At least four exposed collections required for an isolation test.");
            var runtime = factory.Services.GetRequiredService<PgDataHubConfig>();
            runtime.Roles.Single(r => r.Name == "audit_reader").CollectionsAllow = ids.Take(3).ToList();
            runtime.ApiKeys.Add(new() { Id = 2, Name = "audit_reader", Role = "audit_reader", Hash = AuthService.HashKey(readerRaw), Enabled = true });
            await factory.Services.GetRequiredService<AuthService>().RefreshAsync();
            client.DefaultRequestHeaders.Clear();
            string Token(string iss = issuer, string aud = audience, int expires = 20, SecurityKey? signingKey = null, string role = "audit_reader", int starts = -20)
                => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(iss, aud, [new Claim("roles", role)],
                    DateTime.UtcNow.AddMinutes(starts), DateTime.UtcNow.AddMinutes(expires),
                    new SigningCredentials(signingKey ?? key, SecurityAlgorithms.RsaSha256)));
            foreach (var token in new[] { Token(iss: "https://wrong.test"), Token(aud: "wrong"), Token(expires: -10), Token(starts: 10), Token(signingKey: new RsaSecurityKey(wrongRsa)) })
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metadata")).StatusCode);
            }
            foreach (var jwt in new[] { false, true })
            {
                client.DefaultRequestHeaders.Clear();
                if (jwt) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
                else client.DefaultRequestHeaders.Add("X-API-Key", readerRaw);
                using var visible = JsonDocument.Parse(await client.GetStringAsync("/metadata"));
                Assert.Equal(3, visible.RootElement.GetProperty("collections").GetArrayLength());
                foreach (var id in ids)
                foreach (var suffix in new[] { "queryables", "items?limit=0", "items/1" })
                {
                    if (ids.Take(3).Contains(id) && suffix == "items/1") continue;
                    var response = await client.GetAsync($"/collections/{id}/{suffix}");
                    Assert.Equal(ids.Take(3).Contains(id) ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
                }
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/config")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/apikeys")).StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGDATAHUB_CONFIG_PATH", old);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
