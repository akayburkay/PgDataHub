using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PgDataHub.Config;

namespace PgDataHub.Metadata;

public sealed class MetadataListenerService : BackgroundService
{
    private readonly MetadataService _metadataService;
    private readonly ILogger<MetadataListenerService> _logger;

    public MetadataListenerService(
        MetadataService metadataService,
        ILogger<MetadataListenerService> logger)
    {
        _metadataService = metadataService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureTriggerAsync(stoppingToken);

        await using var conn = await Db.OpenConnectionAsync(stoppingToken);

        conn.Notification += (_, e) =>
        {
            _logger.LogInformation(
                "[MetadataListener] PostgreSQL notification received: {Payload}",
                e.Payload);

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation(
                        "[MetadataListener] Refreshing metadata cache...");

                    await _metadataService.RefreshAsync();

                    _logger.LogInformation(
                        "[MetadataListener] Metadata cache refreshed.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[MetadataListener] Metadata refresh failed.");
                }
            });
        };


        await using (var cmd = new NpgsqlCommand(
            "LISTEN metadata_changed;", conn))
        {
            await cmd.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation(
            "[MetadataListener] Listening PostgreSQL metadata changes.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await conn.WaitAsync(stoppingToken);
        }
    }


    private static async Task EnsureTriggerAsync(CancellationToken ct)
    {
        await using var conn = await Db.OpenConnectionAsync(ct);


        var functionSql = """
            CREATE OR REPLACE FUNCTION pgdatahub_metadata_notify()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM pg_notify(
                    'metadata_changed',
                    TG_TAG
                );
            END;
            $$;
            """;

        await using (var cmd = new NpgsqlCommand(functionSql, conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }


        var existsSql = """
            SELECT EXISTS(
                SELECT 1
                FROM pg_event_trigger
                WHERE evtname = 'pgdatahub_metadata_trigger'
            );
            """;

        bool exists;

        await using (var cmd = new NpgsqlCommand(existsSql, conn))
        {
            exists = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }


        if (!exists)
        {
            var triggerSql = """
                CREATE EVENT TRIGGER pgdatahub_metadata_trigger
                ON ddl_command_end
                EXECUTE FUNCTION pgdatahub_metadata_notify();
                """;

            await using var cmd = new NpgsqlCommand(triggerSql, conn);

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}