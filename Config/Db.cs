using Npgsql;
using Npgsql.NetTopologySuite;

namespace PgDataHub.Config;

public static class Db
{
    private static NpgsqlDataSource? _dataSource;

    public static void Initialize(
        DatabaseConfig config)
    {
        if (_dataSource is not null)
            return;

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port,
            Database = config.Database,
            Username = config.User,
            Password = config.Password,

            Pooling = true,

            MinPoolSize = config.MinPoolSize,
            MaxPoolSize = config.MaxPoolSize,

            CommandTimeout = config.CommandTimeout,

            Timeout = 15,

            ConnectionIdleLifetime = 300
        };

        var builder =
            new NpgsqlDataSourceBuilder(
                csb.ConnectionString);

        builder.UseNetTopologySuite();

        _dataSource =
            builder.Build();
    }

    public static ValueTask<NpgsqlConnection>
        OpenConnectionAsync(
            CancellationToken cancellationToken = default)
    {
        if (_dataSource is null)
        {
            throw new InvalidOperationException(
                "Database is not initialized.");
        }

        return _dataSource.OpenConnectionAsync(
            cancellationToken);
    }

    public static NpgsqlDataSource DataSource =>
        _dataSource ??
        throw new InvalidOperationException(
            "Database is not initialized.");
}