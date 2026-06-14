using PgDataHub.Config;
using PgDataHub.Metadata;
using Npgsql;

namespace PgDataHub.Features;

public sealed class FeatureResult : IAsyncDisposable
{
    public CollectionMetadata Collection { get; }
    public NpgsqlDataReader Reader { get; }

    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlCommand _command;
    private bool _disposed;

    internal FeatureResult(
        CollectionMetadata collection,
        NpgsqlConnection connection,
        NpgsqlCommand command,
        NpgsqlDataReader reader)
    {
        Collection = collection;
        _connection = connection;
        _command = command;
        Reader = reader;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await Reader.DisposeAsync();
        await _command.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public sealed class FeatureService
{
    private readonly MetadataService _metadata;
    private readonly NpgsqlDataSource _dataSource;
    private readonly FeatureQueryBuilder _queryBuilder;

    public FeatureService(
        MetadataService metadata,
        NpgsqlDataSource dataSource,
        FeatureQueryBuilder queryBuilder)
    {
        _metadata = metadata;
        _dataSource = dataSource;
        _queryBuilder = queryBuilder;
    }

    public async Task<CollectionMetadata?>
        GetCollectionAsync(
            string collectionId,
            CancellationToken ct = default)
    {
        var dot =
            collectionId.IndexOf('.');

        if (dot <= 0)
            return null;

        var schema =
            collectionId[..dot];

        var table =
            collectionId[(dot + 1)..];

        return await _metadata.GetCollectionAsync(
            schema,
            table,
            ct);
    }

    public async Task<FeatureResult>
        ExecuteAsync(
            FeatureQuery query,
            CancellationToken ct = default)
    {
        var connection =
            await _dataSource
                .OpenConnectionAsync(ct);

        try
        {
            var command =
                new NpgsqlCommand(
                    query.Sql,
                    connection);

            foreach (var parameter in query.Parameters)
            {
                command.Parameters.Add(parameter);
            }

            var reader =
                await command.ExecuteReaderAsync(ct);

            return new FeatureResult(
                query.Collection,
                connection,
                command,
                reader);
        }
        catch (PostgresException ex)
    when (
        ex.MessageText.Contains("Cannot find SRID") ||
        ex.MessageText.Contains("Invalid reserved SRID")
    )
        {
            await connection.DisposeAsync();

            throw new ArgumentException(
                "Invalid 'crs' value.");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<FeatureResult?>
        ExecuteAsync(
            string collectionId,
            CancellationToken ct = default)
    {
        var collection =
            await GetCollectionAsync(
                collectionId,
                ct);

        if (collection is null)
            return null;

        var query =
            _queryBuilder.Build(collection);

        return await ExecuteAsync(
            query,
            ct);
    }
}