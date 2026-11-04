using System.Security.Claims;
using PgDataHub.Config;
using PgDataHub.Metadata;

namespace PgDataHub.Auth;

public sealed class CollectionAccessService(MetadataService metadata, AuthService auth, PgDataHubConfig config)
{
    public async Task<CollectionMetadata?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        var dot = id.IndexOf('.');
        if (dot <= 0) return null;
        var collection = await metadata.GetCollectionAsync(id[..dot], id[(dot + 1)..], ct);
        return collection is null ? null : await ApplyAsync(collection, user, ct);
    }

    public async Task<CollectionMetadata> ApplyAsync(CollectionMetadata collection, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!config.Auth.Enabled) return collection;
        if (!await auth.CanAccessCollectionAsync(user.RoleNames(), collection.Schema, collection.Name, ct))
            throw new CollectionAccessDeniedException();
        var visible = (await auth.ApplyColumnRulesAsync(user.RoleNames(), collection, ct)).Copy();
        visible.PolicyColumns = collection.PolicyColumns ?? collection.Columns;
        visible.RowFilters = RowRule.Resolve(config, user, visible);
        return visible;
    }

    public async Task<IReadOnlyList<CollectionMetadata>> ListAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var result = new List<CollectionMetadata>();
        foreach (var collection in await metadata.GetCollectionsAsync(ct))
        {
            if (config.Auth.Enabled && !await auth.CanAccessCollectionAsync(user.RoleNames(), collection.Schema, collection.Name, ct)) continue;
            result.Add(await ApplyAsync(collection, user, ct));
        }
        return result;
    }
}

public sealed class CollectionAccessDeniedException : Exception;
