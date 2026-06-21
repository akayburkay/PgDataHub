# PgDataHub

A lightweight .NET 10 web server that instantly exposes PostgreSQL tables, views, and functions as a REST API — with full PostGIS / GeoJSON support.

## Features

- **Zero code** — configure `pgdatahub.ini`, run, done
- **PostGIS support** — geometry columns automatically served as GeoJSON `FeatureCollection`
- **CRS transform** — reproject on the fly via `bbox-crs` / `crs` parameters
- **Rich filtering** — equality, comparison, `in`, `between`, `like`, `ilike`, `null` checks
- **Pagination** — `limit` / `offset` with configurable defaults and hard caps
- **Live metadata** — schema changes picked up instantly via PostgreSQL `LISTEN/NOTIFY`
- **Response compression** — Brotli / Gzip for JSON and GeoJSON responses
- **Swagger UI** — available at `/swagger` in development

## Quick Start

```bash
git clone https://github.com/you/PgDataHub.git
cd PgDataHub
# edit pgdatahub.ini
dotnet run
```

## Configuration (`pgdatahub.ini`)

```ini
[database]
host=localhost
port=5432
database=mydb
user=postgres
password=secret

[collections]
include=*
exclude=audit_*

[feature]
defaultLimit=1000
maxLimit=10000

[function]
include=*
defaultTimeout=30
maxRows=10000

# Per-table overrides
[public.buildings.feature]
hide=internal_notes
```

## API Overview

| Endpoint | Description |
|---|---|
| `GET /metadata` | All collections and functions |
| `GET /collections/{schema}.{table}/items` | Query features |
| `GET /collections/{schema}.{table}/items/{id}` | Single feature by primary key |
| `GET /collections/{schema}.{table}/queryables` | Column schema |
| `GET /functions` | List functions |
| `GET /functions/{schema}.{name}/items` | Execute a function |

See [DOCS.md](DOCS.md) for full query parameter and filter reference.

## License

MIT
