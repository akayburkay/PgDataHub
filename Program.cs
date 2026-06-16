using Microsoft.AspNetCore.ResponseCompression;
using Npgsql;
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
   
app.MapControllers();

app.Run();