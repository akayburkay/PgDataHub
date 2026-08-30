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

Returns rows as a `FeatureCollection` (spatial) or `{ "items": [...] }` (non-spatial).

```http
GET /collections/public.cities/items?limit=10&bbox=28.5,40.8,29.5,41.2
```

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": { "type": "Point", "coordinates": [28.97, 41.01] },
      "properties": { "id": 1, "name": "Istanbul" }
    }
  ]
}
```

---

### `GET /collections/{schema}.{table}/items/{id}`

Returns a single feature by primary key. Returns `404` if not found, `400` if the collection has no primary key.

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
| `GET /functions/{schema}.{name}/items?p1=v1` | Execute function |

All `IN` parameters are required. Missing parameters return `400 Bad Request`.

**Return type mapping:**

| PostgreSQL return type | JSON format |
|---|---|
| `SETOF record / table` | `FeatureCollection` or `{ "items": [...] }` |
| `record` (composite) | Single object `{ ... }` |
| `json` / `jsonb` | Raw JSON value |
| `ARRAY` | JSON array |
| Scalar (`int`, `text`, …) | `{ "value": ... }` |

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
| `bbox` | 4 numbers | Spatial bounding box `minx,miny,maxx,maxy` | `bbox=28,40,30,42` |
| `bbox-crs` | SRID | CRS of the bbox coordinates (default: `4326`) | `bbox-crs=EPSG:3857` |
| `crs` | SRID | Target CRS for response geometries | `crs=EPSG:3857` |

CRS values are accepted as `4326`, `EPSG:4326`, or `http://www.opengis.net/def/crs/EPSG/0/4326`.

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
- Column rules merge across the caller's roles: union of allows, intersection of hides
  (most permissive), then layered on top of per-table `[schema.table.feature]` overrides.
- `/metadata` and `/functions` only list resources the caller may see; hidden columns are
  stripped from `/metadata`, `/queryables`, `/items` and `/items/{id}`.

### Admin UI

Open **`/admin/`** in a browser, paste an admin API key, and manage roles and keys:

- **API Keys** — list, create (raw key shown **once**), delete.
- **Roles** — list, create/edit (allow/deny globs + column rules), delete.

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

**Metadata cache + LISTEN/NOTIFY** — Schema queries run only once at startup and again when PostgreSQL notifies of a change. Under high concurrency, there is zero per-request schema overhead.

**Parameterized queries** — All filter values are bound as `NpgsqlParameter`. Types (integer, numeric, uuid, timestamp, boolean) are inferred from column metadata, so no SQL injection is possible.

**Streaming JSON** — Large result sets are written directly to the HTTP response stream via `Utf8JsonWriter` without buffering the full result in memory.

**Response compression** — Brotli (preferred) and Gzip are automatically applied to `application/json` and `application/geo+json` responses.

**Auth in the INI** — Roles, permissions and API keys live in `pgdatahub.ini` and are held in an in-memory cache rebuilt on every management change; the `/admin` UI writes edits straight back to the file (`IniAuthWriter` preserves all other sections and comments). API keys are matched by SHA-256 hash for O(1) lookup; the raw key is never stored. Authentication runs as standard ASP.NET Core schemes (a policy scheme forwards to JWT or API key per request), so authorization reuses `HttpContext.User`.
