using Microsoft.AspNetCore.Http;
using Npgsql;
using PgDataHub.Config;
using PgDataHub.Metadata;
using System.Text.Json;
using NetTopologySuite.Geometries;

namespace PgDataHub.Functions;

public sealed class FunctionService
{
    private readonly MetadataService _metadata;
    private readonly FunctionConfig _config;

    public FunctionService(
        MetadataService metadata,
        PgDataHubConfig config)
    {
        _metadata = metadata;
        _config = config.Function;
    }
    public async Task<IReadOnlyList<FunctionMetadata>> GetFunctionsAsync(CancellationToken ct)
        => await _metadata.GetFunctionsAsync(ct);

    public async Task<FunctionMetadata?> GetFunctionAsync(
        string schema,
        string name,
        CancellationToken ct)
        => await _metadata.GetFunctionAsync(schema, name, ct);

    public async Task<FunctionMetadata?> GetFunctionAsync(uint oid, CancellationToken ct)
        => await _metadata.GetFunctionAsync(oid, ct);

    public async Task ExecuteAsync(
        string schema,
        string name,
        IQueryCollection query,
        HttpResponse response,
        CancellationToken ct)
    {
        var function = await _metadata.GetFunctionAsync(schema, name, ct);

        if (function is null)
            throw new KeyNotFoundException(
                "Function not found or its name is ambiguous; use the function OID endpoint for overloads.");

        await ExecuteAsync(function, query, response, ct);
    }

    public async Task ExecuteAsync(
        uint oid,
        IQueryCollection query,
        HttpResponse response,
        CancellationToken ct)
    {
        var function = await _metadata.GetFunctionAsync(oid, ct)
            ?? throw new KeyNotFoundException("Function not found.");

        await ExecuteAsync(function, query, response, ct);
    }

    private async Task ExecuteAsync(
        FunctionMetadata function,
        IQueryCollection query,
        HttpResponse response,
        CancellationToken ct)
    {

        await using var conn = await Db.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _config.DefaultTimeout;

        var args = BuildArguments(function, query, cmd);
        cmd.CommandText = BuildSql(function, args);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        response.ContentType = "application/json; charset=utf-8";

        await using var writer = new Utf8JsonWriter(response.BodyWriter);

        switch (function.ReturnType)
        {
            case FunctionReturnType.Rowset:
                await WriteRowsetAsync(function, reader, writer, ct);
                break;

            case FunctionReturnType.Composite:
                await WriteSingleObjectAsync(reader, writer, ct);
                break;

            case FunctionReturnType.Scalar:
                await WriteScalarAsync(reader, writer, ct);
                break;

            case FunctionReturnType.Json:
            case FunctionReturnType.Array:
                await WriteRawJsonValueAsync(reader, writer, ct);
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported function return type: {function.ReturnType}");
        }

        await writer.FlushAsync(ct);
    }

    private static string BuildArguments(
        FunctionMetadata function,
        IQueryCollection query,
        NpgsqlCommand cmd)
    {
        var args = new List<string>();

        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var parameter = function.Parameters[i];

            if (!query.TryGetValue(parameter.Name, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Missing parameter: {parameter.Name}");
            }

            var npgsqlName = $"p{i}";
            cmd.Parameters.AddWithValue(npgsqlName, value.ToString());

            args.Add(
                $"{QuoteIdentifier(parameter.Name)} => @{npgsqlName}::{parameter.Type}");
        }

        return string.Join(", ", args);
    }

    private string BuildSql(
    FunctionMetadata function,
    string args)
    {
        var fn =
            $"{QuoteIdentifier(function.Schema)}.{QuoteIdentifier(function.Name)}";

        return function.ReturnType switch
        {
            FunctionReturnType.Rowset =>
    $"select * from {fn}({args}) limit {_config.MaxRows}",

            FunctionReturnType.Composite =>
                $"select * from {fn}({args})",

            FunctionReturnType.Json =>
                $"select {fn}({args})::text as value",

            FunctionReturnType.Array =>
                $"select to_json({fn}({args}))::text as value",

            FunctionReturnType.Scalar =>
                $"select {fn}({args}) as value",

            _ => throw new NotSupportedException()
        };
    }

    private static async Task WriteRowsetAsync(
        FunctionMetadata function,
        NpgsqlDataReader reader,
        Utf8JsonWriter writer,
        CancellationToken ct)
    {
        var geometryColumn =
            function.Columns.FirstOrDefault(c =>
                c.Type.StartsWith("geometry", StringComparison.OrdinalIgnoreCase) ||
                c.Type.StartsWith("geography", StringComparison.OrdinalIgnoreCase));

        writer.WriteStartObject();

        if (geometryColumn is not null)
        {
            writer.WriteString("type", "FeatureCollection");
            writer.WritePropertyName("features");
            writer.WriteStartArray();

            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");

                writer.WritePropertyName("geometry");
                WriteValue(writer, reader[geometryColumn.Name]);

                writer.WritePropertyName("properties");
                writer.WriteStartObject();

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var field = reader.GetName(i);

                    if (string.Equals(
                        field,
                        geometryColumn.Name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writer.WritePropertyName(field);
                    WriteValue(writer, reader.GetValue(i));
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WritePropertyName("items");
            writer.WriteStartArray();

            while (await reader.ReadAsync(ct))
                WriteCurrentRowObject(writer, reader);

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static async Task WriteSingleObjectAsync(
        NpgsqlDataReader reader,
        Utf8JsonWriter writer,
        CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            writer.WriteNullValue();
            return;
        }

        WriteCurrentRowObject(writer, reader);
    }

    private static async Task WriteScalarAsync(
        NpgsqlDataReader reader,
        Utf8JsonWriter writer,
        CancellationToken ct)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("value");

        if (await reader.ReadAsync(ct))
            WriteValue(writer, reader.GetValue(0));
        else
            writer.WriteNullValue();

        writer.WriteEndObject();
    }

    private static async Task WriteRawJsonValueAsync(
        NpgsqlDataReader reader,
        Utf8JsonWriter writer,
        CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(reader.GetString(0));
    }

    private static void WriteCurrentRowObject(
        Utf8JsonWriter writer,
        NpgsqlDataReader reader)
    {
        writer.WriteStartObject();

        for (var i = 0; i < reader.FieldCount; i++)
        {
            writer.WritePropertyName(reader.GetName(i));
            WriteValue(writer, reader.GetValue(i));
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(
    Utf8JsonWriter writer,
    object value)
    {
        if (value is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        if (value is Geometry geometry)
        {
            WriteGeometry(writer, geometry);
            return;
        }

        JsonSerializer.Serialize(writer, value);
    }
    private static void WriteGeometry(
    Utf8JsonWriter writer,
    Geometry geometry)
    {
        writer.WriteStartObject();

        writer.WriteString(
            "type",
            geometry.GeometryType);

        writer.WritePropertyName("coordinates");

        WriteCoordinates(
            writer,
            geometry);

        writer.WriteEndObject();
    }
    private static void WriteCoordinates(
    Utf8JsonWriter writer,
    Geometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                writer.WriteStartArray();
                writer.WriteNumberValue(point.X);
                writer.WriteNumberValue(point.Y);
                writer.WriteEndArray();
                break;

            case MultiPoint multiPoint:
                writer.WriteStartArray();

                foreach (Point p in multiPoint.Geometries)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(p.X);
                    writer.WriteNumberValue(p.Y);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;

            case LineString line:
                WriteLineString(writer, line);
                break;

            case MultiLineString multiLine:
                writer.WriteStartArray();

                foreach (LineString l in multiLine.Geometries)
                {
                    WriteLineString(writer, l);
                }

                writer.WriteEndArray();
                break;

            case Polygon polygon:
                WritePolygon(writer, polygon);
                break;

            case MultiPolygon multiPolygon:
                writer.WriteStartArray();

                foreach (Polygon p in multiPolygon.Geometries)
                {
                    WritePolygon(writer, p);
                }

                writer.WriteEndArray();
                break;

            default:
                throw new NotSupportedException(
                    $"Geometry type {geometry.GeometryType} is not supported.");
        }
    }
    private static void WritePolygon(
    Utf8JsonWriter writer,
    Polygon polygon)
    {
        writer.WriteStartArray();

        WriteLineString(
            writer,
            polygon.ExteriorRing);

        foreach (var ring in polygon.InteriorRings)
        {
            WriteLineString(
                writer,
                ring);
        }

        writer.WriteEndArray();
    }
    private static void WriteLineString(
    Utf8JsonWriter writer,
    LineString line)
    {
        writer.WriteStartArray();

        foreach (var c in line.Coordinates)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(c.X);
            writer.WriteNumberValue(c.Y);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
    private static string QuoteIdentifier(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";
}
