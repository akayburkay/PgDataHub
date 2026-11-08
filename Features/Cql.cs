using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using PgDataHub.Metadata;
using NetTopologySuite.IO;

namespace PgDataHub.Features;

public abstract record CqlNode;
public sealed record CqlProperty(string Name) : CqlNode;
public sealed record CqlLiteral(object? Value) : CqlNode;
public sealed record CqlGeometry(string GeoJson) : CqlNode;
public sealed record CqlWkt(string Wkt) : CqlNode;
public sealed record CqlBbox(double[] Coordinates) : CqlNode;
public sealed record CqlCall(string Op, IReadOnlyList<CqlNode> Args) : CqlNode;

public static class CqlParser
{
    private static readonly Regex Tokens = new(@"\G\s*(?:(?<string>'(?:''|[^'])*')|(?<number>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(?<name>[A-Za-z_][A-Za-z_0-9]*)|(?<op><=|>=|<>|!=|[=<>(),+*/%^-]))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static CqlNode ParseText(string input)
    {
        if (input.Length > 8192) throw new ArgumentException("CQL2 filter is too long.");
        var tokens = new List<string>();
        for (int pos = 0; pos < input.Length;)
        {
            var m = Tokens.Match(input, pos);
            if (!m.Success) { if (string.IsNullOrWhiteSpace(input[pos..])) break; throw new ArgumentException($"Invalid CQL2 token at position {pos}."); }
            tokens.Add(m.Value.TrimStart()); pos += m.Length;
            if (tokens.Count > 512) throw new ArgumentException("CQL2 filter is too complex.");
        }
        return new TextParser(tokens).Parse();
    }
    public static CqlNode ParseJson(string input)
    {
        if (input.Length > 8192) throw new ArgumentException("CQL2 filter is too long.");
        try { using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 }); return Read(doc.RootElement, 0); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { throw new ArgumentException("Invalid CQL2 JSON.", ex); }
    }
    private static CqlNode Read(JsonElement e, int depth)
    {
        if (depth > 24) throw new ArgumentException("CQL2 filter is too deep.");
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("property", out var p) && p.ValueKind == JsonValueKind.String && e.EnumerateObject().Count() == 1)
                return new CqlProperty(p.GetString()!);
            if (e.TryGetProperty("bbox", out var box) && e.EnumerateObject().Count() == 1 && box.ValueKind == JsonValueKind.Array)
                return new CqlBbox(box.EnumerateArray().Select(x => x.GetDouble()).ToArray());
            if (e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && e.TryGetProperty("coordinates", out _) && e.EnumerateObject().Count() == 2)
                return new CqlGeometry(e.GetRawText());
            if (e.TryGetProperty("date", out var date) && e.EnumerateObject().Count() == 1 && date.ValueKind == JsonValueKind.String)
                return new CqlCall("DATE", [new CqlLiteral(date.GetString())]);
            if (e.TryGetProperty("timestamp", out var timestamp) && e.EnumerateObject().Count() == 1 && timestamp.ValueKind == JsonValueKind.String)
                return new CqlCall("TIMESTAMP", [new CqlLiteral(timestamp.GetString())]);
            if (e.TryGetProperty("interval", out var interval) && e.EnumerateObject().Count() == 1 && interval.ValueKind == JsonValueKind.Array && interval.GetArrayLength() == 2)
                return new CqlCall("INTERVAL", interval.EnumerateArray().Select(x => Read(x, depth + 1)).ToArray());
            if (!e.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || !e.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array || e.EnumerateObject().Count() != 2 || args.GetArrayLength() > 128)
                throw new ArgumentException("Invalid CQL2 JSON expression.");
            return new CqlCall(op.GetString()!.ToUpperInvariant(), args.EnumerateArray().Select(x => Read(x, depth + 1)).ToArray());
        }
        return e.ValueKind switch
        {
            JsonValueKind.String => new CqlLiteral(e.GetString()),
            JsonValueKind.Number => new CqlLiteral(e.GetDecimal()),
            JsonValueKind.True => new CqlLiteral(true),
            JsonValueKind.False => new CqlLiteral(false),
            JsonValueKind.Null => new CqlLiteral(null),
            _ => throw new ArgumentException("Unsupported CQL2 JSON literal.")
        };
    }
    private sealed class TextParser(List<string> tokens)
    {
        private int _pos;
        private string? Peek => _pos < tokens.Count ? tokens[_pos] : null;
        private bool Eat(string value) { if (string.Equals(Peek, value, StringComparison.OrdinalIgnoreCase)) { _pos++; return true; } return false; }
        private void Need(string value) { if (!Eat(value)) throw new ArgumentException($"Expected '{value}' in CQL2 filter."); }
        public CqlNode Parse() { var n = Or(0); if (Peek is not null) throw new ArgumentException("Unexpected CQL2 token."); return n; }
        private CqlNode Or(int d) { var n = And(d + 1); while (Eat("OR")) n = new CqlCall("OR", [n, And(d + 1)]); return n; }
        private CqlNode And(int d) { var n = Predicate(d + 1); while (Eat("AND")) n = new CqlCall("AND", [n, Predicate(d + 1)]); return n; }
        private CqlNode Predicate(int d)
        {
            if (d > 24) throw new ArgumentException("CQL2 filter is too deep.");
            if (Eat("NOT")) return new CqlCall("NOT", [Predicate(d + 1)]);
            if (Eat("(")) { var inner = Or(d + 1); Need(")"); return inner; }
            var left = Arithmetic(d + 1);
            if (Eat("IS")) { var neg = Eat("NOT"); Need("NULL"); return new CqlCall(neg ? "IS NOT NULL" : "IS NULL", [left]); }
            var not = Eat("NOT");
            if (Eat("BETWEEN")) { var low = Arithmetic(d + 1); Need("AND"); var high = Arithmetic(d + 1); var n = new CqlCall("BETWEEN", [left, low, high]); return not ? new CqlCall("NOT", [n]) : n; }
            if (Eat("IN"))
            {
                Need("("); var values = new List<CqlNode> { left, Arithmetic(d + 1) };
                while (Eat(",")) { if (values.Count > 128) throw new ArgumentException("CQL2 IN is too large."); values.Add(Arithmetic(d + 1)); }
                Need(")"); var n = new CqlCall("IN", values); return not ? new CqlCall("NOT", [n]) : n;
            }
            if (Eat("LIKE")) { var n = new CqlCall("LIKE", [left, Arithmetic(d + 1)]); return not ? new CqlCall("NOT", [n]) : n; }
            if (not) throw new ArgumentException("Invalid NOT expression.");
            if (Peek is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=") { var op = tokens[_pos++]; return new CqlCall(op, [left, Arithmetic(d + 1)]); }
            if (left is CqlCall) return left;
            throw new ArgumentException("Expected CQL2 predicate.");
        }
        private CqlNode Arithmetic(int d)
        {
            var left = Product(d + 1);
            while (Peek is "+" or "-") { var op = tokens[_pos++]; left = new CqlCall(op, [left, Product(d + 1)]); }
            return left;
        }
        private CqlNode Product(int d)
        {
            var left = Value(d + 1);
            while (Peek is "*" or "/" or "%") { var op = tokens[_pos++]; left = new CqlCall(op, [left, Value(d + 1)]); }
            return left;
        }
        private CqlNode Value(int d)
        {
            if (d > 24 || Peek is null) throw new ArgumentException("Incomplete CQL2 expression.");
            var token = tokens[_pos++];
            if (token == "-") return new CqlCall("NEGATE", [Value(d + 1)]);
            if (token.StartsWith('\'')) return new CqlLiteral(token[1..^1].Replace("''", "'"));
            if (decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return new CqlLiteral(number);
            if (token.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return new CqlLiteral(null);
            if (token.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return new CqlLiteral(true);
            if (token.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return new CqlLiteral(false);
            if (token == "(") { var inner = Arithmetic(d + 1); Need(")"); return inner; }
            if (Eat("("))
            {
                if (token.Equals("BBOX", StringComparison.OrdinalIgnoreCase))
                {
                    var coords = new List<double>();
                    do { var sign = Eat("-") ? -1 : 1; if (Peek is null || !double.TryParse(tokens[_pos++], NumberStyles.Float, CultureInfo.InvariantCulture, out var coordinate)) throw new ArgumentException("Invalid CQL2 BBOX."); coords.Add(sign * coordinate); } while (Eat(","));
                    Need(")"); return new CqlBbox(coords.ToArray());
                }
                if (token.ToUpperInvariant() is "POINT" or "LINESTRING" or "POLYGON" or "MULTIPOINT" or "MULTILINESTRING" or "MULTIPOLYGON")
                {
                    var parts = new List<string> { token, "(" }; var level = 1;
                    while (level > 0 && Peek is not null)
                    {
                        var part = tokens[_pos++];
                        if (part == "-" && Peek is not null && double.TryParse(Peek, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                            part += tokens[_pos++];
                        if (part == "(") level++;
                        if (part == ")") level--;
                        if (level > 8 || parts.Count > 256 || !Regex.IsMatch(part, @"^[(),-]$|^-?\d+(\.\d+)?([eE][+-]?\d+)?$")) throw new ArgumentException("Invalid CQL2 geometry literal.");
                        parts.Add(part);
                    }
                    if (level != 0) throw new ArgumentException("Incomplete CQL2 geometry literal.");
                    return new CqlWkt(string.Join(" ", parts));
                }
                var args = new List<CqlNode>(); if (!Eat(")")) { args.Add(Arithmetic(d + 1)); while (Eat(",")) args.Add(Arithmetic(d + 1)); Need(")"); } return new CqlCall(token.ToUpperInvariant(), args);
            }
            if (!Regex.IsMatch(token, @"^[A-Za-z_][A-Za-z_0-9]*$")) throw new ArgumentException("Invalid CQL2 value.");
            return new CqlProperty(token);
        }
    }
}

public static class CqlSql
{
    public static string Compile(CqlNode root, CollectionMetadata collection, int filterSrid, List<NpgsqlParameter> parameters)
    {
        var columns = collection.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        string Parameter(object value)
        {
            var name = "@cql" + parameters.Count;
            parameters.Add(new NpgsqlParameter(name, value));
            return name;
        }
        string Geometry(CqlNode node)
        {
            var sourceSrid = filterSrid;
            var targetSrid = collection.Srid ?? 4326;
            string expression;
            if (node is CqlGeometry geo)
            {
                using var doc = JsonDocument.Parse(geo.GeoJson);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type is not ("Point" or "LineString" or "Polygon" or "MultiPoint" or "MultiLineString" or "MultiPolygon") ||
                    !doc.RootElement.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Unsupported CQL2 geometry type.");
                static bool Coordinate(JsonElement e) => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() is 2 or 3 && e.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d));
                static bool Sequence(JsonElement e, int min) => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= min && e.EnumerateArray().All(Coordinate);
                var valid = type switch
                {
                    "Point" => Coordinate(coordinates),
                    "LineString" or "MultiPoint" => Sequence(coordinates, type == "LineString" ? 2 : 1),
                    "Polygon" or "MultiLineString" => coordinates.GetArrayLength() > 0 && coordinates.EnumerateArray().All(x => Sequence(x, type == "Polygon" ? 4 : 2)),
                    "MultiPolygon" => coordinates.GetArrayLength() > 0 && coordinates.EnumerateArray().All(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0 && p.EnumerateArray().All(r => Sequence(r, 4))),
                    _ => false
                };
                if (!valid) throw new ArgumentException("Invalid CQL2 geometry coordinates.");
                expression = $"ST_SetSRID(ST_GeomFromGeoJSON({Parameter(geo.GeoJson)}), {sourceSrid})";
            }
            else if (node is CqlWkt wkt)
            {
                try { _ = new WKTReader().Read(wkt.Wkt); }
                catch (ParseException ex) { throw new ArgumentException("Invalid CQL2 geometry literal.", ex); }
                expression = $"ST_GeomFromText({Parameter(wkt.Wkt)}, {sourceSrid})";
            }
            else if (node is CqlBbox box)
            {
                var b = box.Coordinates;
                if (b.Length != 4 || b.Any(x => !double.IsFinite(x)) || b[1] > b[3] || sourceSrid != 4326 && b[0] > b[2] ||
                    sourceSrid == 4326 && (Math.Abs(b[0]) > 180 || Math.Abs(b[2]) > 180 || Math.Abs(b[1]) > 90 || Math.Abs(b[3]) > 90))
                    throw new ArgumentException("Invalid CQL2 bbox.");
                string Envelope(double x1, double x2) => $"ST_MakeEnvelope({Parameter(x1)}, {Parameter(b[1])}, {Parameter(x2)}, {Parameter(b[3])}, {sourceSrid})";
                expression = b[0] > b[2] ? $"ST_Union({Envelope(b[0], 180)}, {Envelope(-180, b[2])})" : Envelope(b[0], b[2]);
            }
            else throw new ArgumentException("Spatial CQL2 operand must be a geometry literal or bbox.");
            return sourceSrid == targetSrid ? expression : $"ST_Transform({expression}, {targetSrid})";
        }
        string CompileNode(CqlNode node, int depth)
        {
            if (depth > 24) throw new ArgumentException("CQL2 filter is too deep.");
            if (node is CqlProperty p)
            {
                if (!columns.TryGetValue(p.Name, out var col)) throw new ArgumentException($"Unknown or hidden CQL2 property '{p.Name}'.");
                return "t.\"" + col.Name.Replace("\"", "\"\"") + "\"";
            }
            if (node is CqlLiteral lit)
            {
                var name = "@cql" + parameters.Count;
                parameters.Add(new NpgsqlParameter(name, lit.Value ?? DBNull.Value));
                return name;
            }
            if (node is not CqlCall call) throw new ArgumentException("Invalid CQL2 node.");
            var op = call.Op.ToUpperInvariant(); var a = call.Args;
            if (op is "S_INTERSECTS" or "S_CONTAINS" or "S_WITHIN" or "S_DISJOINT" or "S_TOUCHES" or "S_CROSSES" or "S_OVERLAPS" or "S_EQUALS" && a.Count == 2)
            {
                if (a[0] is not CqlProperty spatial || !columns.TryGetValue(spatial.Name, out var geomColumn) || !geomColumn.Name.Equals(collection.GeometryColumn, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("CQL2 spatial predicate requires a visible geometry property.");
                var function = op switch { "S_INTERSECTS" => "ST_Intersects", "S_CONTAINS" => "ST_Contains", "S_WITHIN" => "ST_Within", "S_DISJOINT" => "ST_Disjoint", "S_TOUCHES" => "ST_Touches", "S_CROSSES" => "ST_Crosses", "S_OVERLAPS" => "ST_Overlaps", _ => "ST_Equals" };
                return $"{function}({CompileNode(a[0], depth + 1)}, {Geometry(a[1])})";
            }
            if (op is "+" or "-" or "*" or "/" or "%" && a.Count == 2 && a.All(x => x is CqlProperty or CqlLiteral or CqlCall))
            {
                foreach (var numericProperty in a.OfType<CqlProperty>())
                    if (!columns.TryGetValue(numericProperty.Name, out var col) || !IsNumeric(col.Type)) throw new ArgumentException("Arithmetic requires numeric properties.");
                return "(" + CompileNode(a[0], depth + 1) + " " + op + " " + CompileNode(a[1], depth + 1) + ")";
            }
            if (op == "NEGATE" && a.Count == 1) return "(-" + CompileNode(a[0], depth + 1) + ")";
            if (op is "DATE" or "TIMESTAMP" && a.Count == 1 && a[0] is CqlLiteral { Value: string dateText })
            {
                if (op == "DATE" && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return Parameter(date.ToDateTime(TimeOnly.MinValue));
                if (op == "TIMESTAMP" && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)) return Parameter(timestamp.UtcDateTime);
                throw new ArgumentException("Invalid CQL2 temporal literal.");
            }
            if (op is "T_AFTER" or "T_BEFORE" or "T_EQUALS" or "T_INTERSECTS" or "T_DISJOINT" && a.Count == 2 && a[0] is CqlProperty temporal && columns.TryGetValue(temporal.Name, out var temporalColumn) && (temporalColumn.Type == "date" || temporalColumn.Type.Contains("timestamp", StringComparison.OrdinalIgnoreCase)) && a[1] is CqlCall { Op: "DATE" or "TIMESTAMP" })
            {
                var lhs = CompileNode(a[0], depth + 1); var rhs = CompileNode(a[1], depth + 1);
                var sign = op switch { "T_AFTER" => ">", "T_BEFORE" => "<", "T_DISJOINT" => "<>", _ => "=" };
                return $"({lhs} {sign} {rhs})";
            }
            if (op is "AND" or "OR" && a.Count >= 2 && a.Count <= 128)
                return "(" + string.Join(" " + op.ToLowerInvariant() + " ", a.Select(x => CompileNode(x, depth + 1))) + ")";
            if (op == "NOT" && a.Count == 1) return "(not " + CompileNode(a[0], depth + 1) + ")";
            if (op is "IS NULL" or "IS NOT NULL" or "ISNULL" && a.Count == 1 && a[0] is CqlProperty)
                return "(" + CompileNode(a[0], depth + 1) + (op == "IS NOT NULL" ? " is not null)" : " is null)");
            string Operand(CqlNode value, CqlProperty property)
            {
                if (value is not CqlLiteral literal || literal.Value is null) return CompileNode(value, depth + 1);
                var col = columns[property.Name];
                var name = "@cql" + parameters.Count;
                parameters.Add(FeatureQueryBuilder.CreateParameter(name, col, Convert.ToString(literal.Value, CultureInfo.InvariantCulture)!));
                return name;
            }
            if (op is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "LIKE" && a.Count == 2 && a[0] is CqlProperty property && a[1] is CqlProperty or CqlLiteral or CqlCall)
                return "(" + CompileNode(a[0], depth + 1) + " " + (op == "!=" ? "<>" : op) + " " + Operand(a[1], property) + ")";
            if (op is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" && a.Count == 2 && (a[0] is CqlCall || a[1] is CqlCall))
                return "(" + CompileNode(a[0], depth + 1) + " " + (op == "!=" ? "<>" : op) + " " + CompileNode(a[1], depth + 1) + ")";
            if (op == "BETWEEN" && a.Count == 3 && a[0] is CqlProperty && a[1] is CqlLiteral && a[2] is CqlLiteral)
                return "(" + CompileNode(a[0], depth + 1) + " between " + Operand(a[1], (CqlProperty)a[0]) + " and " + Operand(a[2], (CqlProperty)a[0]) + ")";
            if (op == "IN" && a.Count is >= 2 and <= 128 && a[0] is CqlProperty && a.Skip(1).All(x => x is CqlLiteral))
                return "(" + CompileNode(a[0], depth + 1) + " in (" + string.Join(",", a.Skip(1).Select(x => Operand(x, (CqlProperty)a[0]))) + "))";
            throw new ArgumentException($"Unsupported CQL2 operator or operands '{op}'.");
        }
        return CompileNode(root, 0);
    }
    private static bool IsNumeric(string type) => type is "smallint" or "integer" or "bigint" or "real" or "double precision" || type.StartsWith("numeric", StringComparison.OrdinalIgnoreCase) || type.StartsWith("decimal", StringComparison.OrdinalIgnoreCase);
}
