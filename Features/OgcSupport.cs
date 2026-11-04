using System.Globalization;
using System.Text.Json;

namespace PgDataHub.Features;

public sealed record ApiLink(string Href, string Rel, string Type, string? Title = null);

public sealed class FeatureWriteOptions
{
    public bool SingleFeature { get; init; }
    public bool ForceGeoJson { get; init; }
    public int? PageSize { get; init; }
    public Func<bool, IReadOnlyList<ApiLink>>? Links { get; init; }
}

public static class OgcSupport
{
    public const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    public const string Crs84h = "http://www.opengis.net/def/crs/OGC/0/CRS84h";
    public static bool HasHeight(PgDataHub.Metadata.CollectionMetadata collection) =>
        collection.Columns.FirstOrDefault(c => c.Name == collection.GeometryColumn)?.Type.Contains('Z', StringComparison.OrdinalIgnoreCase) == true;
    public static string CrsUri(int srid, bool height = false) => srid == 4326 ? height ? Crs84h : Crs84 : $"http://www.opengis.net/def/crs/EPSG/0/{srid}";
    public static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";
    public static ApiLink Link(HttpRequest request, string path, string rel, string type = "application/json")
        => new(BaseUrl(request) + path, rel, type);

    public static void ValidateFormat(HttpRequest request, bool features = false, string? alternativeMediaType = null)
    {
        var format = request.Query["f"].ToString();
        if (format.Length > 0 && !format.Equals("json", StringComparison.OrdinalIgnoreCase) &&
            !(features && format.Equals("geojson", StringComparison.OrdinalIgnoreCase)))
            throw new UnsupportedRepresentationException("Supported formats are json" + (features ? " and geojson." : "."));
        if (format.Length > 0) return;
        var accepted = request.GetTypedHeaders().Accept;
        if (accepted is { Count: > 0 } && !accepted.Any(a => a.Quality != 0 &&
            (a.MediaType.Value is "*/*" or "application/*" or "application/json" ||
             features && a.MediaType.Value == "application/geo+json" ||
             alternativeMediaType is not null && a.MediaType.Value == alternativeMediaType)))
            throw new UnsupportedRepresentationException("This resource supports JSON representations.");
    }

    public static IReadOnlyList<ApiLink> PageLinks(HttpRequest request, int? pageSize, int offset, bool hasNext)
    {
        var type = "application/geo+json";
        var path = request.Path.Value ?? "";
        var itemIndex = path.LastIndexOf("/items", StringComparison.Ordinal);
        var links = new List<ApiLink>
        {
            new(BaseUrl(request) + request.Path + request.QueryString, "self", type),
            Link(request, itemIndex >= 0 ? path[..itemIndex] : path, "collection")
        };
        if (hasNext && pageSize is > 0)
        {
            var values = request.Query.Where(kv => !kv.Key.Equals("offset", StringComparison.OrdinalIgnoreCase) &&
                !kv.Key.Equals("limit", StringComparison.OrdinalIgnoreCase))
                .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))).ToList();
            values.Add(new("limit", pageSize.Value.ToString(CultureInfo.InvariantCulture)));
            values.Add(new("offset", ((long)offset + pageSize.Value).ToString(CultureInfo.InvariantCulture)));
            links.Add(new(BaseUrl(request) + request.Path + QueryString.Create(values), "next", type));
        }
        return links;
    }

    internal static void WriteLinks(Utf8JsonWriter writer, IReadOnlyList<ApiLink>? links)
    {
        if (links is null) return;
        writer.WriteStartArray("links");
        foreach (var link in links)
        {
            writer.WriteStartObject();
            writer.WriteString("href", link.Href);
            writer.WriteString("rel", link.Rel);
            writer.WriteString("type", link.Type);
            if (link.Title is not null) writer.WriteString("title", link.Title);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

public sealed class UnsupportedRepresentationException(string message) : Exception(message);
