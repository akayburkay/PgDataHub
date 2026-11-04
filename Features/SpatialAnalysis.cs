using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PgDataHub.Metadata;

namespace PgDataHub.Features;

public sealed class SpatialAnalysisRequest
{
    public JsonElement? Geometry { get; init; }
    public int InputCrsSrid { get; init; } = 4326;
    public int? WorkingCrsSrid { get; init; }
    public int? TargetCrsSrid { get; init; }
    public double? Distance { get; init; }
    public double? Tolerance { get; init; }
}

public sealed record AnalysisOperation(string Name, string Description, bool RequiresGeometry, string Result);
public sealed record SpatialAnalysisSpec(string Operation, SpatialAnalysisRequest Request);

internal sealed record AnalysisPlan(string Geometry, int GeometrySrid, int OutputSrid,
    string? Value = null, string? Predicate = null, string Prefix = "", string Join = "");

public static class SpatialAnalysis
{
    public static IReadOnlyList<AnalysisOperation> Operations { get; } = [
        new("buffer", "Buffer each authorized feature. Distance uses working CRS units; negative distances shrink polygons.", false, "geometry"),
        new("centroid", "Centroid of each authorized feature.", false, "geometry"),
        new("envelope", "Axis-aligned envelope in the working CRS.", false, "geometry"),
        new("convex-hull", "Convex hull of each authorized feature.", false, "geometry"),
        new("boundary", "Boundary of each authorized feature.", false, "geometry"),
        new("simplify", "Topology-preserving simplification. Tolerance uses working CRS units.", false, "geometry"),
        new("transform", "Transform to targetCrsSrid; response Content-Crs identifies the output CRS.", false, "geometry"),
        new("intersection", "Intersection with the supplied GeoJSON geometry, per authorized feature.", true, "geometry"),
        new("union", "Pairwise union with the supplied GeoJSON geometry; this does not dissolve the entire collection.", true, "geometry"),
        new("difference", "Subtract the supplied GeoJSON geometry from each authorized feature.", true, "geometry"),
        new("area", "Planar area in squared working CRS units, returned as properties.analysisValue.", false, "number"),
        new("length", "Planar length in working CRS units, returned as properties.analysisValue.", false, "number"),
        new("distance", "Distance to the supplied geometry in working CRS units, as properties.analysisValue.", true, "number"),
        new("intersects", "Return authorized features intersecting the supplied geometry; uses the source spatial index when no working CRS transform is needed.", true, "boolean"),
        new("dwithin", "Return authorized features within distance of the supplied geometry. Distance uses working CRS units.", true, "boolean") ];

    internal static AnalysisPlan Build(CollectionMetadata collection, SpatialAnalysisSpec spec,
        int? responseSrid, List<NpgsqlParameter> parameters)
    {
        if (!collection.IsSpatial || collection.GeometryColumn is null)
            throw new ArgumentException("Spatial analysis requires a visible geometry column.");
        var operation = Operations.FirstOrDefault(o => o.Name.Equals(spec.Operation, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown analysis operation. See /analysis/operations.");
        var request = spec.Request;
        var nativeSrid = collection.Srid ?? 4326;
        var workingSrid = request.WorkingCrsSrid ?? nativeSrid;
        if (request.InputCrsSrid <= 0 || workingSrid <= 0 || request.TargetCrsSrid is <= 0)
            throw new ArgumentException("Analysis SRIDs must be positive EPSG identifiers.");
        string Parameter(string name, object value, NpgsqlDbType type)
        {
            parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            return "@" + name;
        }
        string Numeric(double? value, string name, bool nonNegative = false)
        {
            if (value is null || !double.IsFinite(value.Value) || nonNegative && value < 0)
                throw new ArgumentException($"A finite {(nonNegative ? "nonnegative " : "")}{name} is required.");
            return Parameter("analysis_" + name, value.Value, NpgsqlDbType.Double);
        }
        var source = "t." + Quote(collection.GeometryColumn);
        var working = workingSrid == nativeSrid ? source : $"ST_Transform({source}, {Parameter("analysis_working_srid", workingSrid, NpgsqlDbType.Integer)})";
        var input = "analysis_input.geom";
        var prefix = "";
        var join = "";
        if (operation.RequiresGeometry)
        {
            if (request.Geometry is not { ValueKind: JsonValueKind.Object } geometry ||
                !geometry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("Point" or "LineString" or "Polygon" or "MultiPoint" or "MultiLineString" or "MultiPolygon" or "GeometryCollection"))
                throw new ArgumentException("A GeoJSON geometry object is required; Features and FeatureCollections are not input geometries.");
            var json = Parameter("analysis_geometry", geometry.GetRawText(), NpgsqlDbType.Text);
            var srid = Parameter("analysis_input_srid", request.InputCrsSrid, NpgsqlDbType.Integer);
            var expression = $"ST_SetSRID(ST_GeomFromGeoJSON({json}), {srid})";
            if (request.InputCrsSrid != workingSrid)
                expression = $"ST_Transform({expression}, {Parameter("analysis_operand_srid", workingSrid, NpgsqlDbType.Integer)})";
            prefix = $"with analysis_input as materialized (select {expression} as geom)\n";
            join = " cross join analysis_input";
        }
        string? value = null;
        string? predicate = null;
        var resultSrid = workingSrid;
        var geometryResult = operation.Name switch
        {
            "buffer" => $"ST_Buffer({working}, {Numeric(request.Distance, "distance")})",
            "centroid" => $"ST_Centroid({working})",
            "envelope" => $"ST_Envelope({working})",
            "convex-hull" => $"ST_ConvexHull({working})",
            "boundary" => $"ST_Boundary({working})",
            "simplify" => $"ST_SimplifyPreserveTopology({working}, {Numeric(request.Tolerance, "tolerance", true)})",
            "intersection" => $"ST_Intersection({working}, {input})",
            "union" => $"ST_Union({working}, {input})",
            "difference" => $"ST_Difference({working}, {input})",
            _ => working
        };
        if (operation.Name == "transform")
        {
            var target = request.TargetCrsSrid ?? throw new ArgumentException("targetCrsSrid is required for transform.");
            geometryResult = $"ST_Transform({working}, {Parameter("analysis_target_srid", target, NpgsqlDbType.Integer)})";
            resultSrid = target;
        }
        value = operation.Name switch
        {
            "area" => $"ST_Area({working})",
            "length" => $"ST_Length({working})",
            "distance" => $"ST_Distance({working}, {input})",
            "intersects" => predicate = $"ST_Intersects({working}, {input})",
            "dwithin" => predicate = $"ST_DWithin({working}, {input}, {Numeric(request.Distance, "distance", true)})",
            _ => null
        };
        if (value is not null)
        {
            if (collection.Columns.Any(c => c.Name.Equals("analysisValue", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The collection already has an analysisValue property.");
            geometryResult = source;
            resultSrid = nativeSrid;
        }
        return new(geometryResult, resultSrid, responseSrid ?? (operation.Name == "transform" ? resultSrid : 4326), value, predicate, prefix, join);
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
