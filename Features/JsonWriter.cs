using System.Text.Json;
using Npgsql;

namespace PgDataHub.Features;

public static class JsonWriter
{
    private delegate void CellWriter(Utf8JsonWriter w, NpgsqlDataReader r, int ordinal);

    public static async Task WriteAsync(HttpResponse response, FeatureResult result,
        CancellationToken ct = default, FeatureWriteOptions? options = null)
    {
        options ??= new FeatureWriteOptions();
        var reader = result.Reader;
        var geometryOrdinal = result.Collection.IsSpatial
            ? GetGeometryOrdinal(reader, result.Collection.GeometryColumn) : -1;
        var geojson = geometryOrdinal >= 0 || options.ForceGeoJson || options.SingleFeature;
        response.ContentType = geojson ? "application/geo+json" : "application/json";
        await using var writer = new Utf8JsonWriter(response.Body);
        var plan = BuildPlan(reader, geometryOrdinal);
        var idOrdinal = GetGeometryOrdinal(reader, result.Collection.PrimaryKey);

        if (options.SingleFeature)
        {
            if (!await reader.ReadAsync(ct)) { response.StatusCode = 404; return; }
            WriteFeature(writer, reader, geometryOrdinal, idOrdinal, plan, options.Links?.Invoke(false));
            await writer.FlushAsync(ct);
            return;
        }

        writer.WriteStartObject();
        if (geojson) writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray(geojson ? "features" : "items");
        var returned = 0;
        var hasNext = false;
        long? totalCount = null;
        var totalCountOrdinal = GetTotalCountOrdinal(reader);
        while (await reader.ReadAsync(ct))
        {
            if (options.PageSize is { } size && returned >= size) { hasNext = size > 0; break; }
            if (returned == 0 && totalCountOrdinal >= 0 && !reader.IsDBNull(totalCountOrdinal))
                totalCount = reader.GetInt64(totalCountOrdinal);
            if (geojson) WriteFeature(writer, reader, geometryOrdinal, idOrdinal, plan, null);
            else
            {
                writer.WriteStartObject();
                WritePlanFields(writer, reader, plan);
                writer.WriteEndObject();
            }
            returned++;
            if (returned % 64 == 0) await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        writer.WriteNumber("numberReturned", returned);
        if (totalCount is not null) writer.WriteNumber("numberMatched", totalCount.Value);
        OgcSupport.WriteLinks(writer, options.Links?.Invoke(hasNext));
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static void WriteFeature(Utf8JsonWriter writer, NpgsqlDataReader reader, int geometryOrdinal,
        int idOrdinal, (string Name, CellWriter Write)[] plan, IReadOnlyList<ApiLink>? links)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        if (idOrdinal >= 0 && !reader.IsDBNull(idOrdinal))
        {
            writer.WritePropertyName("id");
            ResolveWriter(reader.GetFieldType(idOrdinal), reader.GetDataTypeName(idOrdinal))(writer, reader, idOrdinal);
        }
        writer.WritePropertyName("geometry");
        WriteGeometry(writer, reader, geometryOrdinal);
        writer.WriteStartObject("properties");
        WritePlanFields(writer, reader, plan);
        writer.WriteEndObject();
        OgcSupport.WriteLinks(writer, links);
        writer.WriteEndObject();
    }

        private static void WritePlanFields(
            Utf8JsonWriter writer,
            NpgsqlDataReader reader,
            (string Name, CellWriter Write)[] plan)
        {
            for (var i = 0; i < plan.Length; i++)
            {
                var (name, write) = plan[i];
                if (name is null) continue; 

                writer.WritePropertyName(name);

                if (reader.IsDBNull(i))
                    writer.WriteNullValue();
                else
                    write(writer, reader, i);
            }
        }

        private static (string Name, CellWriter Write)[] BuildPlan(
            NpgsqlDataReader reader,
            int geometryOrdinal)
        {
            var plan = new (string Name, CellWriter Write)[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i == geometryOrdinal ||
        reader.GetName(i).Equals(
            FeatureQuery.TotalCountColumn,
            StringComparison.OrdinalIgnoreCase))
                {
                    plan[i] = (null!, static (_, _, _) => { });
                    continue;
                }

                plan[i] = (reader.GetName(i), ResolveWriter(reader.GetFieldType(i), reader.GetDataTypeName(i)));
            }

            return plan;
        }

        private static CellWriter ResolveWriter(Type clrType, string dataType)
        {
            if (dataType is "json" or "jsonb")
                return static (w, r, i) =>
                    w.WriteRawValue(r.GetFieldValue<string>(i), skipInputValidation: true);

            if (clrType == typeof(string))
                return static (w, r, i) => w.WriteStringValue(r.GetFieldValue<string>(i));

            if (clrType == typeof(bool))
                return static (w, r, i) => w.WriteBooleanValue(r.GetFieldValue<bool>(i));

            if (clrType == typeof(int))
                return static (w, r, i) => w.WriteNumberValue(r.GetFieldValue<int>(i));

            if (clrType == typeof(long))
                return static (w, r, i) => w.WriteNumberValue(r.GetFieldValue<long>(i));

            if (clrType == typeof(short))
                return static (w, r, i) => w.WriteNumberValue(r.GetFieldValue<short>(i));

            if (clrType == typeof(decimal))
                return static (w, r, i) => w.WriteNumberValue(r.GetFieldValue<decimal>(i));

            if (clrType == typeof(double))
                return static (w, r, i) =>
                {
                    var v = r.GetFieldValue<double>(i);
                    if (double.IsNaN(v) || double.IsInfinity(v)) w.WriteNullValue();
                    else w.WriteNumberValue(v);
                };

            if (clrType == typeof(float))
                return static (w, r, i) =>
                {
                    var v = r.GetFieldValue<float>(i);
                    if (float.IsNaN(v) || float.IsInfinity(v)) w.WriteNullValue();
                    else w.WriteNumberValue(v);
                };

            if (clrType == typeof(DateTime))
                return static (w, r, i) => w.WriteStringValue(r.GetFieldValue<DateTime>(i));

            if (clrType == typeof(Guid))
                return static (w, r, i) => w.WriteStringValue(r.GetFieldValue<Guid>(i));

            if (clrType == typeof(byte[]))
                return static (w, r, i) => w.WriteBase64StringValue(r.GetFieldValue<byte[]>(i));

            if (clrType.IsArray)
                return static (w, r, i) => WriteArray(w, (Array)r.GetValue(i)!);

            return static (w, r, i) => w.WriteStringValue(r.GetValue(i)?.ToString());
        }

        private static void WriteArray(Utf8JsonWriter writer, Array array)
        {
            writer.WriteStartArray();
            foreach (var item in array)
            {
                switch (item)
                {
                    case null: writer.WriteNullValue(); break;
                    case string s: writer.WriteStringValue(s); break;
                    case bool b: writer.WriteBooleanValue(b); break;
                    case int or long or short: writer.WriteNumberValue(Convert.ToInt64(item)); break;
                    case decimal dec: writer.WriteNumberValue(dec); break;
                    case double or float:
                        var d = Convert.ToDouble(item);
                        if (double.IsNaN(d) || double.IsInfinity(d)) writer.WriteNullValue();
                        else writer.WriteNumberValue(d);
                        break;
                    case DateTime dt: writer.WriteStringValue(dt); break;
                    case Guid g: writer.WriteStringValue(g); break;
                    default: writer.WriteStringValue(item.ToString()); break;
                }
            }
            writer.WriteEndArray();
        }

        private static int GetGeometryOrdinal(NpgsqlDataReader reader, string? geometryColumn)
        {
            for (var i = 0; i < reader.FieldCount; i++)
                if (reader.GetName(i).Equals(geometryColumn, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
        private static int GetTotalCountOrdinal(NpgsqlDataReader reader)
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (reader.GetName(i).Equals(
                        FeatureQuery.TotalCountColumn,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
        private static void WriteGeometry(Utf8JsonWriter writer, NpgsqlDataReader reader, int ordinal)
        {
            if (ordinal < 0 || reader.IsDBNull(ordinal))
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteRawValue(reader.GetFieldValue<string>(ordinal), skipInputValidation: true);
        }
    }
