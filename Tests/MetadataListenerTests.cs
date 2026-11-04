using System.Threading.Channels;
using PgDataHub.Metadata;
using PgDataHub.Config;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace PgDataHub.Tests;

public sealed class MetadataListenerTests
{
    [Fact]
    public async Task Polling_refreshes_without_any_database_notifications()
    {
        var channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var poller = MetadataListenerService.PollAsync(channel.Writer, TimeSpan.FromMilliseconds(20), cancellation.Token);
        var refresher = MetadataListenerService.ProcessChangesAsync(channel.Reader, TimeSpan.FromMilliseconds(1), _ =>
        {
            if (++calls == 2) twice.TrySetResult();
            return Task.CompletedTask;
        }, ex => throw ex, cancellation.Token);
        await twice.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresher);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task Missing_trigger_privilege_rolls_back_installation_and_keeps_listen_available()
    {
        JsonWriterTests.InitializeDatabase();
        var database = "pgdh_test_" + Guid.NewGuid().ToString("N");
        var role = database + "_role";
        await using var admin = await Db.OpenConnectionAsync();
        async Task AdminSql(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            await AdminSql($"create database {database}");
            await AdminSql($"create role {role} nologin nosuperuser");
            var csb = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database, Pooling = false };
            // The opened connection may hide its password; the fixture uses the local test configuration.
            var config = PgDataHubConfig.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../pgdatahub.ini")));
            csb.Password = config.Database.Password;
            await using var connection = new NpgsqlConnection(csb.ConnectionString);
            await connection.OpenAsync();
            await using (var grant = new NpgsqlCommand($"grant create on schema public to {role}; set role {role}", connection))
                await grant.ExecuteNonQueryAsync();
            var service = new MetadataListenerService(new MetadataService(config, NullLogger<MetadataService>.Instance),
                NullLogger<MetadataListenerService>.Instance, new ConfigurationBuilder().Build());
            Assert.False(await service.EnsureTriggerAsync(connection, CancellationToken.None));
            await using (var listen = new NpgsqlCommand("LISTEN metadata_changed", connection))
                await listen.ExecuteNonQueryAsync();
            await using var check = new NpgsqlCommand("select to_regprocedure('public.pgdatahub_metadata_notify()') is null", connection);
            Assert.Equal(true, await check.ExecuteScalarAsync());
        }
        finally
        {
            await AdminSql($"drop database if exists {database} with (force)");
            await AdminSql($"drop role if exists {role}");
        }
    }

    [Fact]
    public async Task Ddl_burst_is_coalesced_and_changes_during_refresh_are_retained()
    {
        var channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var worker = MetadataListenerService.ProcessChangesAsync(channel.Reader, TimeSpan.FromMilliseconds(20), async ct =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { first.TrySetResult(); await release.Task.WaitAsync(ct); }
            else second.TrySetResult();
        }, ex => throw ex, cancellation.Token);
        for (var i = 0; i < 1000; i++) channel.Writer.TryWrite(0);
        await first.Task.WaitAsync(cancellation.Token);
        Assert.Equal(1, calls);
        for (var i = 0; i < 1000; i++) channel.Writer.TryWrite(0);
        release.TrySetResult();
        await second.Task.WaitAsync(cancellation.Token);
        channel.Writer.Complete();
        await worker;
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Refresh_error_does_not_stop_next_poll_or_notification()
    {
        var channel = Channel.CreateBounded<byte>(1);
        var calls = 0;
        var errors = 0;
        channel.Writer.TryWrite(0);
        await MetadataListenerService.ProcessChangesAsync(channel.Reader, TimeSpan.FromMilliseconds(1), _ =>
        {
            if (++calls == 1) throw new InvalidOperationException("transient failure");
            channel.Writer.Complete();
            return Task.CompletedTask;
        }, _ => { errors++; channel.Writer.TryWrite(0); }, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(1, errors);
    }
}
