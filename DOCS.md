# PgDataHub — Documentation

## Table of Contents

- [Installation](#installation)
- [Configuration Reference](#configuration-reference)
- [API Reference](#api-reference)
- [Query Parameters](#query-parameters)
- [Filtering](#filtering)
- [Authentication & Authorization](#authentication--authorization)
- [Architecture](#architecture)

---

## Installation

The recommended installation is Docker Compose, which starts PgDataHub and PostGIS together and persists both configuration and database data. See [DOCKER.md](DOCKER.md) for first installation, updates, backups, production security and troubleshooting.

For a local development run, install .NET 10 SDK, configure `pgdatahub.ini`, and run `dotnet run`.

## Configuration Reference

Configuration is loaded from `pgdatahub.ini` (or `PGDATAHUB_CONFIG_PATH`). Management changes are written atomically and applied to the in-memory authorization/metadata state immediately; a restart is not required.

### `[database]`

| Key | Default | Description |
|---|---|---|
| `host` | `localhost` | PostgreSQL host |
| `port` | `5432` | PostgreSQL port |
| `database` | — | Database name |
| `user` | — | Username |
| `password` | — | Password |
| `minPoolSize` | `5` | Connection pool minimum |
| `maxPoolSize` | `100` | Connection pool maximum |
| `commandTimeout` | `30` | Query timeout (seconds) |

### `[collections]`

| Key | Default | Description |
|---|---|---|
| `include` | `*` | Tables/views to expose (comma-separated, supports glob) |
| `exclude` | — | Tables/views to hide (e.g. `audit_*, tmp_*`) |

### `[feature]`

| Key | Default | Description |
|---|---|---|
| `defaultLimit` | `1000` | Row limit when `limit` is not specified |
| `maxLimit` | `10000` | Hard cap — requests cannot exceed this |

### `[function]`

| Key | Default | Description |
|---|---|---|
| `include` | `*` | Functions to expose |
| `exclude` | — | Functions to hide |
| `volatileInclude` | empty | Explicit opt-in globs for VOLATILE functions; `include`/`exclude` still apply |
| `defaultTimeout` | `30` | Execution timeout (seconds) |
| `maxRows` | `10000` | Max rows for rowset functions |

### Per-table overrides: `[schema.table.feature]`

| Key | Description |
|---|---|
| `hide` | Columns to exclude from responses (comma-separated) |
| `allow` | Only return these columns (empty = all) |

```ini
[public.employees.feature]
hide=salary, ssn
```

---

## API Reference

Collection IDs follow the format `schema.table` (e.g. `public.cities`).

### OGC discovery and dynamic OpenAPI

| Endpoint | Description |
|---|---|
| `GET /` | Public landing page with `data`, `conformance`, `service-desc` and `service-doc` links |
| `GET /conformance` | Public declaration of the implemented Core/GeoJSON/OpenAPI profile |
| `GET /collections` | Collections visible to the caller, with `id`, `itemType` and resource links |
| `GET /collections/{id}` | One authorized collection description |
| `GET /api` or `/openapi.json` | OpenAPI 3.0 document generated from current authorized metadata |
| `GET /swagger/` | Swagger UI, available in Production as well as Development |

Connect an OGC API – Features client using the API root URL and configure its API key header or JWT credentials. Landing/conformance documents contain no protected metadata; collection discovery and feature access retain API authentication. This implements the discovery, GeoJSON and paging contract from [OGC API – Features Part 1](https://docs.ogc.org/is/17-069r4/17-069r4.html). An external OGC certification suite and desktop QGIS/ArcGIS interoperability have not been run. HTML/GML encodings and the full Part 2 CRS conformance class are not advertised.

`f=json` and `f=geojson` are accepted on feature resources (both return GeoJSON); metadata uses `f=json`. Unsupported `f` or incompatible `Accept` returns `406`. Coordinates default to longitude/latitude WGS84 (CRS84), independent of the storage SRID. Explicit `crs` is supported on both items and individual features; `Content-Crs` identifies the output. Queryables use `application/schema+json`.

Items include `numberReturned`, `links` and, with `count=true`, `numberMatched` when available. Follow `links[rel=next].href`; it preserves filters, format and CRS and uses the effective capped page size. The last page has no next link. Paging reads at most one extra row and does not issue a count query by default. Discovery reads cached metadata and does not run full-table extent scans.

The dynamic specification includes concrete collection/item/queryable paths, column types, filters, all 15 analysis operations and authorized function OID paths. It omits forbidden collections and hidden columns. The specification requires API authentication or a valid existing admin session; sign in at `/admin/` before opening Swagger in a browser. It is generated per caller and is marked `private, no-store`. `/swagger/v1/swagger.json` is an alias for the same protected document.

### `GET /metadata`

Returns all collections and functions with their column/parameter metadata.

```json
{
  "collections": [
    {
      "schema": "public",
      "name": "cities",
      "type": "BASE TABLE",
      "isSpatial": true,
      "geometryColumn": "geom",
      "geometryType": "MULTIPOLYGON",
      "srid": 4326,
      "primaryKey": "id",
      "columns": [
        { "name": "id",   "type": "integer", "nullable": false },
        { "name": "name", "type": "text",    "nullable": true  }
      ]
    }
  ],
  "functions": []
}
```

---

### `GET /collections/{schema}.{table}/items`

Returns a GeoJSON `FeatureCollection`. Non-spatial collections have null feature geometries. A visible primary key is also emitted as the top-level feature `id`.

```http
GET /collections/public.cities/items?limit=10&bbox=28.5,40.8,29.5,41.2
```

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "id": 1,
      "geometry": { "type": "Point", "coordinates": [28.97, 41.01] },
      "properties": { "id": 1, "name": "Istanbul" }
    }
  ],
  "numberReturned": 1,
  "links": [{ "rel": "self", "type": "application/geo+json", "href": "https://api.example/collections/public.cities/items?limit=10" }]
}
```

---

### `GET /collections/{schema}.{table}/items/{id}`

Returns one GeoJSON `Feature` by primary key, rather than a FeatureCollection wrapper. Returns `404` for absent or row-restricted records and `400` when the collection has no visible single-column primary key. This and the unified items response replace the earlier single-item FeatureCollection/non-spatial items wrappers.

---

### `GET /collections/{schema}.{table}/queryables`

Returns the JSON Schema for all queryable columns.

```json
{
  "title": "public.cities",
  "type": "object",
  "properties": {
    "id":   { "type": "integer" },
    "geom": { "type": "object", "format": "geometry", "geometryType": "MULTIPOLYGON", "srid": 4326 }
  }
}
```

---

### Functions

| Endpoint | Description |
|---|---|
| `GET /functions` | List all exposed functions |
| `GET /functions/{schema}.{name}` | Function metadata |
| `GET /functions/{schema}.{name}/items?p1=v1` | Execute a STABLE/IMMUTABLE function |
| `POST /functions/{schema}.{name}/items?p1=v1` | Execute a function; VOLATILE requires explicit opt-in and write permission |

The same rules apply to `/functions/{oid}/items`. A VOLATILE function is hidden by default, even with `include=*`. Opt in with `[function] volatileInclude=public.write_*`, then grant both `functions.allow=public.write_*` and `functions.volatile.allow=public.write_*` in the caller's `[role.NAME]` section. `functions.volatile.deny` overrides a matching write allow. Neither admin roles nor `functions.allow=*` implicitly grant write permission. VOLATILE execution requires authentication to be enabled and an API key/JWT caller; the default anonymous role cannot execute writes. Parameters remain in the query string for POST. GET returns `405` and `Allow: POST` for exposed VOLATILE functions without invoking them. STABLE/IMMUTABLE execution runs in a read-only PostgreSQL transaction, protecting against stale volatility metadata too.

All `IN` parameters are required. Missing parameters return `400 Bad Request`.

**Return type mapping:**

| PostgreSQL return type | JSON format |
|---|---|
| `SETOF record / table` | `FeatureCollection` or `{ "items": [...] }` |
| `record` (composite) | Single object `{ ... }` |
| `json` / `jsonb` | Raw JSON value |
| `ARRAY` | JSON array |
| Scalar (`int`, `text`, …) | `{ "value": ... }` |

### Spatial analysis

`GET /analysis/operations` lists 15 read-only operations. Use `POST /collections/{id}/analysis/{operation}` with a JSON body (`{}` for operations needing no operands). Collection, column and row rules apply before results and counts are produced. Query parameters support the same limits, property filters, bbox, select and ordering as items. No geometry or schema changes are written to PostgreSQL.

| Operation | Body fields | Result |
|---|---|---|
| `buffer` | `distance` | Buffered geometry; negative values shrink polygons |
| `centroid` | — | Centroid geometry |
| `envelope` | — | Bounding geometry in the working CRS |
| `convex-hull` | — | Convex hull |
| `boundary` | — | Geometry boundary |
| `simplify` | `tolerance` (nonnegative) | Topology-preserving simplification |
| `transform` | `targetCrsSrid` | Reprojected geometry |
| `intersection` | `geometry` | Intersection per feature |
| `union` | `geometry` | Pairwise union per feature, without dissolving the collection |
| `difference` | `geometry` | Input geometry subtracted from each feature |
| `area` | — | `properties.analysisValue`, in squared working CRS units |
| `length` | — | `properties.analysisValue`, in working CRS units (line length) |
| `distance` | `geometry` | `properties.analysisValue`, in working CRS units |
| `intersects` | `geometry` | Matching features with boolean `analysisValue` |
| `dwithin` | `geometry`, `distance` (nonnegative) | Features within distance, with boolean `analysisValue` |

`geometry` must be a GeoJSON Geometry, not a Feature/FeatureCollection. `inputCrsSrid` defaults to `4326`. `workingCrsSrid` defaults to the stored collection SRID; set a suitable projected CRS for metric measurements. Geometry distances use CRS units, so measurements in EPSG:4326 are in degrees, and areas in square degrees; see [PostGIS ST_Buffer](https://postgis.net/docs/ST_Buffer.html). `crs` sets response coordinates independently of measurement units. `transform` defaults the response to `targetCrsSrid`; other operations default to CRS84.

```http
POST /collections/public.roads/analysis/dwithin?limit=25
Content-Type: application/json
X-API-Key: YOUR_KEY

{"geometry":{"type":"Point","coordinates":[29,41]},"inputCrsSrid":4326,"distance":100,"workingCrsSrid":32635}
```

Operands are parameterized and materialized once per query. `intersects` and `dwithin` can use the existing source spatial index when the working CRS equals the storage CRS; transforming the indexed column may require a functional index, so prefer transforming the operand to the source CRS when possible. Analysis responses are bounded pages; repeat POST with `offset` and the same body to continue (no GET next link is advertised for POST bodies). Large geometries remain subject to the existing 1 MB request limit and database command timeout. Analysis never performs an unbounded collection dissolve.

---

## Query Parameters

Applies to all `GET /collections/.../items` endpoints.

| Parameter | Type | Description | Example |
|---|---|---|---|
| `limit` | integer | Max rows to return | `limit=50` |
| `offset` | integer | Rows to skip (pagination) | `offset=100` |
| `select` | list | Columns to return (`*` = all) | `select=id,name` |
| `orderby` | list | Sort order. Prefix `-` for DESC, `+` or none for ASC | `orderby=-pop,name` |
| `distinct` | boolean | Remove duplicate rows | `distinct=true` |
| `count` | boolean | Include total count as `__total_count` | `count=true` |
| `f` | string | JSON/GeoJSON feature representation | `f=geojson` |
| `bbox` | 4 or 6 numbers | Spatial bounding box `minx,miny,[minz],maxx,maxy,[maxz]`; longitude may wrap across the antimeridian | `bbox=28,40,30,42` |
| `datetime` | string | RFC3339 instant or closed/half-open interval | `datetime=2026-01-01T00:00:00Z/..` |
| `bbox-crs` | SRID | CRS of the bbox coordinates (default: `4326`) | `bbox-crs=EPSG:3857` |
| `crs` | SRID | Target CRS for response geometries | `crs=EPSG:3857` |

CRS values are accepted as `4326`, `EPSG:4326`, or `http://www.opengis.net/def/crs/EPSG/0/4326`.

`CRS84`, `CRS84h` and their OGC URIs are also recognized. Six-coordinate bboxes include a vertical range; features without a spatial geometry still match a bbox. Temporal filtering uses the first visible `date` or `timestamp` column in metadata order, with inclusive bounds and UTC comparisons (timestamps without a timezone are treated as UTC). An instant matches the whole day for a `date` column. Null temporal values and collections without a visible temporal property match valid datetime filters. Invalid or reversed intervals return `400`.

When a visible single-column primary key (or discovered unique key) is available, results default to that key in ascending order. Explicit `orderby` adds the key as a final tie breaker unless already present. This also works when `select` omits the key. For `distinct=true` with a projection that omits the key, the projected values determine the default order, preserving deduplication. Collections with no visible discovered key need an explicit unique `orderby` for deterministic pagination. Concurrent inserts/deletes can still shift offset pages.

### Browser access and reverse proxies

CORS defaults to any origin for GET/HEAD/OPTIONS, accepts API key/Authorization headers and does not allow browser credentials. Configure `Cors__AllowedOrigins__0=https://maps.example.com` (and subsequent numbered entries) to restrict origins and enable other methods, including POST, from those origins. These settings use ASP.NET configuration/environment variables and require a restart.

Forwarded headers run before rate limiting and authentication. Loopback proxies are trusted by default. Add the actual proxy IP using `ReverseProxy__KnownProxies__0=172.30.0.2`, or a dedicated proxy CIDR using `ReverseProxy__KnownNetworks__0=172.30.0.0/24`. `ReverseProxy__ForwardLimit` defaults to `1`. Untrusted peers cannot change the client address with `X-Forwarded-For`. The proxy should set `X-Forwarded-For` and `X-Forwarded-Proto`.

### Metadata refresh

The listener continues LISTEN when event trigger installation is unavailable (for example, a managed PostgreSQL account without superuser privileges). Installation is transactional; permission failures leave no orphan notify function. An existing trigger is reused without replacing its function. Periodic refresh runs every 60 seconds, including as a fallback for missed notifications. Set `Metadata__PollIntervalSeconds` to change the interval. DDL notifications are coalesced in a 500 ms window (`Metadata__DebounceMilliseconds`) with one refresh worker and at most one pending refresh, so migrations do not queue a reload per statement. These environment settings require a restart.

---

## Filtering

Any query parameter that is not a reserved key is treated as a property filter.

### Operators

| Syntax | Operator | Example |
|---|---|---|
| `?col=val` | Equal | `?status=active` |
| `?col!=val` | Not equal | `?status!=inactive` |
| `?col>=val` | Greater or equal | `?age>=18` |
| `?col<=val` | Less or equal | `?price<=100` |
| `?col>val` | Greater than | `?score>50` |
| `?col<val` | Less than | `?stock<10` |
| `?col=v1,v2` | IN | `?status=active,pending` |
| `?col!=v1,v2` | NOT IN | `?city!=Paris,Berlin` |
| `?col=min..max` | BETWEEN | `?price=10..50` |
| `?col=null` | IS NULL | `?deleted_at=null` |
| `?col!=null` | IS NOT NULL | `?email!=null` |
| `?col=*pat*` | ILIKE (wildcard) | `?name=*york*` |
| `?col!=*pat*` | NOT ILIKE | `?name!=*test*` |
| `?col~==pat` | ILIKE (raw pattern) | `?name~==Ali%` |
| `?col!~==pat` | NOT ILIKE | `?name!~==tmp%` |

> Wildcards: `*` → SQL `%`, `?` → SQL `_`

### Examples

```http
# Cities named Istanbul or Ankara
GET /collections/public.cities/items?name=Istanbul,Ankara

# Products priced between 10 and 50, sorted by price
GET /collections/public.products/items?price=10..50&orderby=price

# Active users whose name starts with "Ali", page 2
GET /collections/public.users/items?name=Ali*&status=active&limit=20&offset=20

# Non-deleted orders, newest first, with total count
GET /collections/public.orders/items?deleted_at=null&orderby=-created_at&count=true
```

---

## Authentication & Authorization

Auth is **off by default**. When enabled, every endpoint requires a valid credential
and access is scoped by role. **All auth data — roles, permissions and API keys — is
stored in `pgdatahub.ini`** (the `[role.*]` and `[apikey.*]` sections) and managed from
the bundled `/admin` web UI. No database tables are involved.

### Configuration

```ini
[auth]
enabled=true
defaultRole=            # role for requests with no credentials; empty = 401
adminRole=admin         # role required for the /admin UI and /auth/* endpoints

[auth.apikey]
enabled=true
header=X-API-Key

[auth.jwt]
enabled=true
authority=https://idp.example.com/realms/main
audience=pgdatahub
roleClaim=roles         # JWT claim whose values are role names
```

A request is authenticated as **JWT** when it carries `Authorization: Bearer <token>`,
otherwise the configured **API key header** is used. JWT role claim values map directly
to role names.

### Data in the INI

**Roles** — one `[role.<name>]` section each:

```ini
[role.readonly]
description=Public cities only
collections.allow=public.cities, public.roads*
collections.deny=public.secret
functions.allow=
columns.public.cities.hide=population
columns.public.cities.allow=
```

| Key | Meaning |
|---|---|
| `collections.allow` / `functions.allow` | Glob whitelist over `schema.name` (`*`, `public.*`, `public.cities`) |
| `collections.deny` / `functions.deny` | Glob blacklist; **deny wins** over allow |
| `columns.<pattern>.allow` | Column whitelist for that collection pattern (empty = all) |
| `columns.<pattern>.hide` | Columns to drop from responses |
| `rows` | JSON array of structured collection row rules; managed visually in the admin role editor |

### Row authorization without database policy changes

In **Roles & access → Edit role → Row access rules**, select a collection/pattern, column and operator. Choose a fixed value or a JWT claim name, then add more AND conditions as needed. Collection/column pickers use the existing metadata lookup. Saving writes the rules to the INI and applies them immediately; no PostgreSQL roles, policies or migrations are installed.

The role management API accepts the same structured rules:

```json
{
  "collectionsAllow": ["public.orders"],
  "columnRules": [{"pattern":"public.orders","hide":["tenant_id"]}],
  "rowRules": [{
    "pattern": "public.orders",
    "conditions": [{"column":"tenant_id","operator":"eq","claim":"tenant_id","values":[]}]
  }]
}
```

For API key roles, use a fixed `values:["42"]` instead of `claim`. Allowed operators are `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `in`, `notIn`, `between`, `isNull` and `isNotNull`. Claim conditions use `eq`/`in`; multiple values of a verified JWT claim form an IN list. Missing claims, unknown policy columns and invalid typed values grant no rows for that role. Geometry columns cannot be row-rule operands.

All matching row rules within a role are ANDed; grants from different collection-granting roles are ORed. A granting role without a matching row rule grants all rows. A role without collection access contributes no row grant, and collection deny rules still override every allow. Hidden tenant columns can constrain rows without being exposed or made available as user filters. SQL values remain parameters; raw SQL predicates are not accepted.

Policies apply in SQL to collection items, single-item lookups, counts, next-page detection and spatial analyses. They do not rewrite opaque PostgreSQL function bodies; function permissions remain separate, so only grant functions whose own behavior is appropriate for the role. Direct database access is outside these API policies. Disabling authentication disables role restrictions.

**API keys** — one `[apikey.<id>]` section each; only the SHA-256 **hash** is stored:

```ini
[apikey.1]
name=admin key
owner=ops
description=
hash=6aa3a725…            ; encode(sha256('raw-key'), 'hex')
role=admin
enabled=true
expires=
lastUsed=
```

`lastUsed` is updated (throttled to once per minute per key) on each successful request.

**Decision rules:**
- Access is granted if any matching `allow` exists and no matching `deny` does.
- Column rules merge across the caller's roles: union of allows and hides; a hidden
  column stays hidden, layered on top of per-table `[schema.table.feature]` overrides.
- `/metadata` and `/functions` only list resources the caller may see; hidden columns are
  stripped from `/metadata`, `/queryables`, `/items` and `/items/{id}`.

### Admin UI

The administration console supports **Türkçe / English**. Choose the language on the sign-in page, sidebar or any dialog. The selection is saved in this browser; the first visit follows the browser's preferred supported language. Switching is immediate and preserves open form drafts, row conditions and lookup selections. Labels, help text, notifications, validation messages and date/number presentation are localized. Database identifiers, role names, user descriptions and API payload values remain unchanged. Translation runs only when UI fragments are created or the language changes; it adds no API/database requests.

Open **`/admin/`** in a browser, paste an admin API key, and manage roles and keys:

- **API Keys** — list, create (raw key shown **once**), delete.
- **Roles** — list, create/edit (allow/deny globs, column rules and visual row rules), delete.

Every change is written straight back to `pgdatahub.ini` and applied immediately (no
restart). The page itself is unauthenticated (it holds no data); its calls require the
admin role. The same operations are available as a REST API:

| Endpoint | Description |
|---|---|
| `GET/POST /auth/apikeys`, `DELETE /auth/apikeys/{id}` | Manage keys (create returns the raw key once) |
| `GET /auth/roles`, `PUT /auth/roles/{name}`, `DELETE /auth/roles/{name}` | Manage roles (delete is `409` if a key still uses the role) |

An unknown `roleName` when creating a key returns `400`.

### Responses

| Situation | Status |
|---|---|
| Missing/invalid credential (and no `defaultRole`) | `401 Unauthorized` |
| Authenticated but not permitted for the resource | `403 Forbidden` |

```http
GET /collections/public.cities/items
X-API-Key: my-raw-key
```

---

## Architecture

```
PgDataHub/
├── Program.cs                      # DI setup, middleware pipeline
├── pgdatahub.ini                   # Configuration file
│
├── wwwroot/admin/index.html        # Bundled auth admin UI (served at /admin/)
│
├── Config/
│   ├── PgDataHubConfig.cs          # INI parser and all config models (incl. auth data)
│   ├── IniAuthWriter.cs            # Writes [role.*]/[apikey.*] back into the INI
│   ├── Glob.cs                     # Shared glob matcher
│   └── Db.cs                       # NpgsqlDataSource singleton (connection pool)
│
├── Metadata/
│   ├── MetadataService.cs          # In-memory cache for collections and functions
│   ├── MetadataListenerService.cs  # PostgreSQL LISTEN/NOTIFY for live schema updates
│   ├── MetadataModels.cs           # CollectionMetadata, FunctionMetadata, etc.
│   ├── MetadataSql.cs              # Raw SQL against information_schema / pg_catalog
│   └── MetadataController.cs       # GET /metadata
│
├── Features/
│   ├── FeatureQueryBuilder.cs      # SQL generator (filters, bbox, CRS, pagination)
│   ├── FeatureService.cs           # Collection resolution and query execution
│   ├── JsonWriter.cs               # Streams NpgsqlDataReader → GeoJSON/JSON
│   └── FeatureController.cs        # GET /collections/...
│
├── Functions/
│   ├── FunctionService.cs          # Function execution and JSON/GeoJSON output
│   └── FunctionController.cs       # GET /functions/...
│
└── Auth/
    ├── AuthModels.cs                   # Principal, RolePermission, ApiKeyRecord, DTOs
    ├── AuthService.cs                  # In-memory auth cache (from INI) + access decisions
    ├── ApiKeyAuthenticationHandler.cs  # API key authentication scheme
    ├── AccessControl.cs                # Role claim type + ClaimsPrincipal helpers
    ├── AdminControllerBase.cs          # Shared admin-role guard
    ├── AuthController.cs               # /auth/apikeys management
    └── RolesController.cs              # /auth/roles management
```

### Key Design Decisions

**Metadata cache + LISTEN/NOTIFY** — Schema metadata is cached, refreshed after coalesced PostgreSQL notifications and periodically reconciled. If event trigger installation is unavailable, LISTEN and polling continue. Requests read the shared cache without querying schema metadata.

**Parameterized queries** — All filter values are bound as `NpgsqlParameter`. Types (integer, numeric, uuid, timestamp, boolean) are inferred from column metadata, so no SQL injection is possible.

**Streaming JSON** — Large result sets are written directly to the HTTP response stream via `Utf8JsonWriter` without buffering the full result in memory.

**Response compression** — Brotli (preferred) and Gzip are automatically applied to `application/json` and `application/geo+json` responses.

**Auth in the INI** — Roles, permissions and API keys live in `pgdatahub.ini` and are held in an in-memory cache rebuilt on every management change; the `/admin` UI writes edits straight back to the file (`IniAuthWriter` preserves all other sections and comments). API keys are matched by SHA-256 hash for O(1) lookup; the raw key is never stored. Authentication runs as standard ASP.NET Core schemes (a policy scheme forwards to JWT or API key per request), so authorization reuses `HttpContext.User`.
