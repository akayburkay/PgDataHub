using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Npgsql;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Features;
using PgDataHub.Functions;
using PgDataHub.Metadata;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

var config =
    PgDataHubConfig.Load("pgdatahub.ini");

Db.Initialize(config.Database);

builder.Services.AddSingleton(config);

builder.Services.AddSingleton<NpgsqlDataSource>(
    _ => Db.DataSource);

builder.Services.AddSingleton<MetadataService>();

builder.Services.AddSingleton<FeatureQueryBuilder>();

builder.Services.AddScoped<FeatureService>();
builder.Services.AddScoped<FunctionService>();
builder.Services.AddHostedService<MetadataListenerService>();

builder.Services.AddSingleton<AuthService>();

if (config.Auth.Enabled)
{
    var authn = builder.Services.AddAuthentication("PgDataHub");

    var jwtEnabled = config.Auth.Jwt.Enabled;
    var apiKeyEnabled = config.Auth.ApiKey.Enabled;

    // Choose scheme per request: Bearer token -> JWT, otherwise -> API key.
    // Never forward to a scheme that isn't registered.
    authn.AddPolicyScheme("PgDataHub", displayName: null, options =>
    {
        options.ForwardDefaultSelector = ctx =>
        {
            var isBearer = ctx.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

            if (isBearer && jwtEnabled)
                return JwtBearerDefaults.AuthenticationScheme;
            if (apiKeyEnabled)
                return ApiKeyAuthenticationHandler.SchemeName;

            return jwtEnabled
                ? JwtBearerDefaults.AuthenticationScheme
                : ApiKeyAuthenticationHandler.SchemeName;
        };
    });

    if (config.Auth.ApiKey.Enabled)
    {
        authn.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
    }

    if (config.Auth.Jwt.Enabled)
    {
        authn.AddJwtBearer(options =>
        {
            options.Authority = config.Auth.Jwt.Authority;
            options.Audience = config.Auth.Jwt.Audience;
            options.TokenValidationParameters.RoleClaimType = config.Auth.Jwt.RoleClaim;
        });
    }

    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}

builder.Services.AddControllers();

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;

    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();

    options.MimeTypes =
        ResponseCompressionDefaults.MimeTypes.Concat(
        [
            "application/json",
            "application/geo+json"
        ]);
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve the bundled admin UI from wwwroot (e.g. /admin/). Static files are not
// subject to the authorization fallback policy; the page itself holds no data and
// its API calls require an admin key.
app.UseDefaultFiles();
app.UseStaticFiles();

if (config.Auth.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapControllers();

app.Run();