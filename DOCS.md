# PgDataHub — Documentation

## Table of Contents

- [Configuration Reference](#configuration-reference)
- [API Reference](#api-reference)
- [Query Parameters](#query-parameters)
- [Filtering](#filtering)
- [Architecture](#architecture)

---

## Configuration Reference

All configuration is read from `pgdatahub.ini` at startup.

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

## Architecture

```
PgDataHub/
├── Program.cs                      # DI setup, middleware pipeline
├── pgdatahub.ini                   # Configuration file
│
├── Config/
│   ├── PgDataHubConfig.cs          # INI parser and all config models
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
└── Functions/
    ├── FunctionService.cs          # Function execution and JSON/GeoJSON output
    └── FunctionController.cs       # GET /functions/...
```

### Key Design Decisions

**Metadata cache + LISTEN/NOTIFY** — Schema queries run only once at startup and again when PostgreSQL notifies of a change. Under high concurrency, there is zero per-request schema overhead.

**Parameterized queries** — All filter values are bound as `NpgsqlParameter`. Types (integer, numeric, uuid, timestamp, boolean) are inferred from column metadata, so no SQL injection is possible.

**Streaming JSON** — Large result sets are written directly to the HTTP response stream via `Utf8JsonWriter` without buffering the full result in memory.

**Response compression** — Brotli (preferred) and Gzip are automatically applied to `application/json` and `application/geo+json` responses.
