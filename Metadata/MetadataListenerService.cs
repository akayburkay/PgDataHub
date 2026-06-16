using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PgDataHub.Metadata;

public sealed class MetadataListenerService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly MetadataService _metadata;
    private readonly ILogger<MetadataListenerService> _logger;

    public MetadataListenerService(
        NpgsqlDataSource dataSource,
        MetadataService metadata,
        ILogger<MetadataListenerService> logger)
    {
        _dataSource = dataSource;
        _metadata = metadata;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(stoppingToken);

        connection.Notification += async (_, e) =>
        {
            if (e.Channel != "pgdatahub_metadata")
                return;

            _logger.LogInformation(
                "[Metadata] PostgreSQL schema change detected. Refreshing cache.");

            try
            {
                await _metadata.RefreshAsync(stoppingToken);

                _logger.LogInformation(
                    "[Metadata] Cache refresh completed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[Metadata] Cache refresh failed.");
            }
        };

        await using (var command = new NpgsqlCommand(
            "LISTEN pgdatahub_metadata;",
            connection))
        {
            await command.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation(
            "[Metadata] Listening PostgreSQL channel: pgdatahub_metadata");

        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken);
        }

        _logger.LogInformation(
            "[Metadata] Listener stopped.");
    }
}