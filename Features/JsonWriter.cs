    using System.Text.Json;
    using Microsoft.AspNetCore.Http;
    using Npgsql;

    namespace PgDataHub.Features;

    public static class JsonWriter
    {
        private delegate void CellWriter(Utf8JsonWriter w, NpgsqlDataReader r, int ordinal);

        public static Task WriteAsync(
        HttpResponse response,
        FeatureResult result,
        CancellationToken ct = default)
        {
            if (!result.Collection.IsSpatial)
            {
                return WriteJsonAsync(
                    response,
                    result.Reader,
                    ct);
            }

            var geometryOrdinal =
                GetGeometryOrdinal(
                    result.Reader);

            return geometryOrdinal >= 0
                ? WriteGeoJsonAsync(
                    response,
                    result.Reader,
                    ct)
                : WriteJsonAsync(
                    response,
                    result.Reader,
                    ct);
        }

    private static async Task WriteJsonAsync(
HttpResponse response,
NpgsqlDataReader reader,
CancellationToken ct)
    {
        response.ContentType = "application/json";

        await using var writer = new Utf8JsonWriter(response.Body);

        var plan = BuildPlan(reader, geometryOrdinal: -1);
        var totalCountOrdinal = GetTotalCountOrdinal(reader);

        if (!await reader.ReadAsync(ct))
        {
            writer.WriteStartObject();
            writer.WriteNumber("numberReturned", 0);
            writer.WriteStartArray("items");
            writer.WriteEndArray();
            writer.WriteEndObject();

            await writer.FlushAsync(ct);
            return;
        }

        long? totalCount = null;

        if (totalCountOrdinal >= 0 &&
            !reader.IsDBNull(totalCountOrdinal))
        {
            totalCount = reader.GetInt64(totalCountOrdinal);
        }

        var returned = 1;

        writer.WriteStartObject();

        if (totalCount is not null)
        {
            writer.WriteNumber("numberMatched", totalCount.Value);
        }

        writer.WriteStartArray("items");

        writer.WriteStartObject();
        WritePlanFields(writer, reader, plan);
        writer.WriteEndObject();

        while (await reader.ReadAsync(ct))
        {
            returned++;

            writer.WriteStartObject();
            WritePlanFields(writer, reader, plan);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteNumber("numberReturned", returned);

        writer.WriteEndObject();

        await writer.FlushAsync(ct);
    }

    private static async Task WriteGeoJsonAsync(
            HttpResponse response,
            NpgsqlDataReader reader,
            CancellationToken ct)
        {
            response.ContentType = "application/geo+json";

            await using var writer = new Utf8JsonWriter(response.Body);

            var geometryOrdinal = GetGeometryOrdinal(reader);
            var plan = BuildPlan(reader, geometryOrdinal);

            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");

                writer.WritePropertyName("geometry");
                WriteGeometry(writer, reader, geometryOrdinal);

                writer.WriteStartObject("properties");
                WritePlanFields(writer, reader, plan);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            await writer.FlushAsync(ct);
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

        private static int GetGeometryOrdinal(NpgsqlDataReader reader)
        {
            for (var i = 0; i < reader.FieldCount; i++)
                if (reader.GetName(i).Equals("geometry", StringComparison.OrdinalIgnoreCase))
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