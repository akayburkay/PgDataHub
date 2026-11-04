using System.Threading.Channels;
using Npgsql;
using PgDataHub.Config;

namespace PgDataHub.Metadata;

public sealed class MetadataListenerService : BackgroundService
{
    private readonly MetadataService _metadataService;
    private readonly ILogger<MetadataListenerService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _debounce;

    public MetadataListenerService(MetadataService metadataService,
        ILogger<MetadataListenerService> logger, IConfiguration configuration)
    {
        _metadataService = metadataService;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Metadata:PollIntervalSeconds", 60)));
        _debounce = TimeSpan.FromMilliseconds(Math.Max(1, configuration.GetValue("Metadata:DebounceMilliseconds", 500)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, Db.ChangeToken);
            try
            {
                await using var conn = await Db.OpenConnectionAsync(linked.Token);
                var triggerInstalled = await EnsureTriggerAsync(conn, linked.Token);
                var changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
                {
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite
                });
                conn.Notification += (_, _) => changes.Writer.TryWrite(0);
                await using (var cmd = new NpgsqlCommand("LISTEN metadata_changed;", conn))
                    await cmd.ExecuteNonQueryAsync(linked.Token);
                _logger.LogInformation("[MetadataListener] LISTEN active; trigger installed: {Installed}; polling every {Seconds}s.",
                    triggerInstalled, _pollInterval.TotalSeconds);

                var listener = ListenAsync(conn, linked.Token);
                var poller = PollAsync(changes.Writer, _pollInterval, linked.Token);
                var refresher = ProcessChangesAsync(changes.Reader, linked.Token);
                changes.Writer.TryWrite(0);
                try { await await Task.WhenAny(listener, poller, refresher); }
                finally
                {
                    await linked.CancelAsync();
                    try { await Task.WhenAll(listener, poller, refresher); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MetadataListener] Listener failed; retrying.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task ListenAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) await connection.WaitAsync(ct);
    }

    internal static async Task PollAsync(ChannelWriter<byte> writer, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct)) writer.TryWrite(0);
    }

    private async Task ProcessChangesAsync(ChannelReader<byte> reader, CancellationToken ct)
        => await ProcessChangesAsync(reader, _debounce, _metadataService.RefreshAsync,
            ex => _logger.LogError(ex, "[MetadataListener] Metadata refresh failed."), ct);

    internal static async Task ProcessChangesAsync(ChannelReader<byte> reader, TimeSpan debounce,
        Func<CancellationToken, Task> refresh, Action<Exception> onError, CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct))
        {
            // Bound the coalescing window so continuous DDL cannot starve refreshes.
            await Task.Delay(debounce, ct);
            while (reader.TryRead(out _)) { }
            try { await refresh(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { onError(ex); }
        }
    }

    internal async Task<bool> EnsureTriggerAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand("""
            SELECT evtenabled::text FROM pg_event_trigger
            WHERE evtname = 'pgdatahub_metadata_trigger';
            """, conn);
        if (await exists.ExecuteScalarAsync(ct) is string enabled)
            return enabled != "D";

        try
        {
            // Roll back function creation too if the trigger cannot be installed.
            await using var transaction = await conn.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("""
                CREATE OR REPLACE FUNCTION public.pgdatahub_metadata_notify()
                RETURNS event_trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('metadata_changed', TG_TAG);
                END;
                $$;
                CREATE EVENT TRIGGER pgdatahub_metadata_trigger
                ON ddl_command_end
                EXECUTE FUNCTION public.pgdatahub_metadata_notify();
                """, conn, transaction);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege
            or PostgresErrorCodes.FeatureNotSupported or PostgresErrorCodes.DuplicateObject)
        {
            _logger.LogWarning("[MetadataListener] Trigger installation unavailable ({SqlState}); continuing LISTEN and periodic polling.", ex.SqlState);
            return false;
        }
    }
}
