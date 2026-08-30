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
using PgDataHub;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging.EventLog;

var builder = WebApplication.CreateBuilder(args);

// Windows' default EventLog provider can throw when the process runs under a
// locked-down service identity. Logging must never take the API down.
if (OperatingSystem.IsWindows())
    builder.Logging.AddFilter<EventLogLoggerProvider>(level => false);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1024 * 1024;
});

// PgDataHub admin sessions are server-side random tokens and do not rely on
// Data Protection. Avoid an implicit per-user key repository that can fail for
// restricted Windows service accounts.
builder.Services.AddDataProtection();
builder.Services.Configure<KeyManagementOptions>(options =>
    options.XmlRepository = new InMemoryXmlRepository());

var configPath = Environment.GetEnvironmentVariable("PGDATAHUB_CONFIG_PATH") ?? "pgdatahub.ini";
var config = PgDataHubConfig.Load(configPath);
DockerConfigBootstrap.Apply(config);

Db.Initialize(config.Database);

builder.Services.AddSingleton(config);

builder.Services.AddSingleton<MetadataService>();
builder.Services.AddSingleton<ConfigService>();

builder.Services.AddSingleton<FeatureQueryBuilder>();

builder.Services.AddScoped<FeatureService>();
builder.Services.AddScoped<FunctionService>();
builder.Services.AddHostedService<MetadataListenerService>();

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AdminSessionService>();

var authn = builder.Services.AddAuthentication("PgDataHub");
authn.AddPolicyScheme("PgDataHub", displayName: null, options =>
{
    options.ForwardDefaultSelector = ctx =>
    {
        var bearer = ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        return bearer && config.Auth.Jwt.Enabled
            ? JwtBearerDefaults.AuthenticationScheme
            : ApiKeyAuthenticationHandler.SchemeName;
    };
});
authn.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
authn.AddJwtBearer(options =>
{
    options.Authority = config.Auth.Jwt.Authority;
    options.Audience = config.Auth.Jwt.Audience;
    options.TokenValidationParameters.RoleClaimType = config.Auth.Jwt.RoleClaim;
});
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAssertion(context => !config.Auth.Enabled || context.User.Identity?.IsAuthenticated == true)
        .Build());

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            AutoReplenishment = true
        }));
});

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
var serverTimingEnabled = builder.Configuration.GetValue("Diagnostics:ServerTiming", false);

app.UseExceptionHandler();
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    if (response.ContentLength is > 0 || !string.IsNullOrEmpty(response.ContentType)) return;
    var status = response.StatusCode;
    var title = status switch
    {
        401 => "Authentication required", 403 => "Forbidden", 404 => "Resource not found",
        429 => "Too many requests", _ => "Request failed"
    };
    await response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        Status = status, Title = title,
        Detail = status == 401 ? "Valid credentials are required."
               : status == 403 ? "You do not have permission to access this resource."
               : status == 429 ? "Too many requests. Try again later."
               : "The request could not be completed.",
        Instance = statusContext.HttpContext.Request.Path,
        Extensions = { ["errorId"] = statusContext.HttpContext.TraceIdentifier }
    });
});
app.UseResponseCompression();
app.UseRateLimiter();
if (serverTimingEnabled) app.Use(async (context, next) =>
{
    RequestTiming.Begin(context);
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["Server-Timing"] = RequestTiming.Format(context);
        return Task.CompletedTask;
    });
    await next();
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    }
    await next();
});

// Serve the bundled admin UI from wwwroot (e.g. /admin/). Static files are not
// subject to the authorization fallback policy; the page itself holds no data and
// its API calls require an admin key.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    // Swagger exposes the complete API inventory. Keep it behind the same
    // authentication boundary as the API when authentication is enabled.
    app.UseWhen(context => context.Request.Path.StartsWithSegments("/swagger"), branch =>
    {
        branch.Use(async (context, next) =>
        {
            if (config.Auth.Enabled && context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync();
                return;
            }
            await next();
        });
    });
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();
