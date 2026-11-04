using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PgDataHub.Auth;
using PgDataHub.Metadata;

namespace PgDataHub.Features;

[ApiController]
public sealed class OgcController(CollectionAccessService access) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult Landing()
    {
        OgcSupport.ValidateFormat(Request);
        return Ok(new { title = "PgDataHub", description = "OGC API – Features and PostgreSQL spatial analysis",
            links = new[] {
                OgcSupport.Link(Request, "/", "self"),
                OgcSupport.Link(Request, "/conformance", "conformance"),
                OgcSupport.Link(Request, "/collections", "data"),
                OgcSupport.Link(Request, "/api", "service-desc", "application/vnd.oai.openapi+json;version=3.0"),
                OgcSupport.Link(Request, "/swagger/index.html", "service-doc", "text/html") } });
    }

    [AllowAnonymous]
    [HttpGet("/conformance")]
    public IActionResult Conformance()
    {
        OgcSupport.ValidateFormat(Request);
        return Ok(new { conformsTo = new[] {
            "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
            "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
            "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30" } });
    }

    [HttpGet("/collections")]
    public async Task<IActionResult> Collections(CancellationToken ct)
    {
        OgcSupport.ValidateFormat(Request);
        return Ok(new { links = new[] { OgcSupport.Link(Request, "/collections", "self") },
            collections = (await access.ListAsync(User, ct)).Select(c => Describe(c, Request)) });
    }

    [HttpGet("/collections/{collectionId}")]
    public async Task<IActionResult> Collection(string collectionId, CancellationToken ct)
    {
        OgcSupport.ValidateFormat(Request);
        var collection = await access.GetAsync(collectionId, User, ct);
        return collection is null ? NotFound() : Ok(Describe(collection, Request));
    }

    internal static object Describe(CollectionMetadata collection, HttpRequest request)
    {
        var id = $"{collection.Schema}.{collection.Name}";
        var path = "/collections/" + Uri.EscapeDataString(id);
        return new { id, title = id, description = collection.Type, itemType = "feature",
            // No full-table extent scan on discovery; extents are optional in Core.
            crs = new[] { OgcSupport.CrsUri(4326, OgcSupport.HasHeight(collection)) },
            links = new[] { OgcSupport.Link(request, path, "self"),
                OgcSupport.Link(request, path + "/items", "items", "application/geo+json"),
                OgcSupport.Link(request, path + "/queryables", "http://www.opengis.net/def/rel/ogc/1.0/queryables", "application/schema+json") } };
    }
}
