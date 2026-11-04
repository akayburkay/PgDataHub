# PgDataHub

PgDataHub is a .NET 10 service that exposes PostgreSQL tables, views, and functions as a secure REST API without application code. It includes PostGIS/GeoJSON support, live metadata, API key/JWT authorization, and a built-in administration console.

## Highlights

- **Zero application code:** Configure it through `pgdatahub.ini` or the administration console.
- **PostGIS and GeoJSON:** Geometry columns are automatically returned as GeoJSON `FeatureCollection` responses, with bbox filtering and CRS transformation.
- **Rich queries:** Filtering, sorting, column selection, pagination, distinct results, and counts.
- **OGC discovery:** Landing page, conformance, collections, GeoJSON features and next-page links for GIS clients.
- **Row policies:** Fixed values or JWT claims, managed in the admin role editor without database policy changes.
- **Spatial analysis:** 15 operations including buffering, overlays, measurements and indexed distance searches.
- **Dynamic OpenAPI:** Production Swagger and concrete collection/function paths generated from authorized metadata.
- **Live metadata:** Matching schema changes are detected through PostgreSQL `LISTEN/NOTIFY` without restarting the service.
- **Security:** API key and JWT authentication, role-based table/view/function and column permissions, secure admin sessions, and CSRF protection.
- **PostgreSQL functions:** Parameterized and overloaded functions are distinguished by OID and signature.
- **Performance:** Metadata caching, connection pooling, streaming JSON, compression, and optional `Server-Timing` diagnostics.

## Quick Start

### Docker Compose (recommended)

Requirements: Docker Desktop on Windows/macOS, or Docker Engine with Compose v2 on Linux.

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
cp .env.example .env
# Replace both replace-with-... values in .env
docker compose up -d
```

On Windows PowerShell, copy the environment file with `Copy-Item .env.example .env`.

After startup:

- Administration console: <http://localhost:5270/admin/>
- Readiness check: <http://localhost:5270/health/ready>

Sign in with the `PGDATAHUB_ADMIN_KEY` value from `.env`. Compose starts PgDataHub and PostGIS, waits for PostgreSQL readiness, persists the database and configuration in named volumes, and automatically restarts the services.

```bash
docker compose ps
docker compose logs -f pgdatahub
curl -H "X-API-Key: YOUR_ADMIN_KEY" http://localhost:5270/metadata
```

> Never use the example passwords in production. Compose publishes PgDataHub only on `127.0.0.1` by default. Add a TLS-terminating reverse proxy and appropriate network rules before exposing it externally.

See the [Docker installation guide](DOCKER.md) for upgrades, backups, production security, and troubleshooting.

### Local .NET

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
# Configure the PostgreSQL connection in pgdatahub.ini
dotnet run
```

Requirements: .NET 10 SDK and an accessible PostgreSQL 14+ server. PostGIS is required for geometry support.

## Basic Configuration (`pgdatahub.ini`)

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

[public.buildings.feature]
hide=internal_notes
```

Changes saved through the administration console are written atomically and applied without a restart. In Docker installations, configuration is stored in the `pgdatahub_config` volume.

## API Overview

| Endpoint | Description |
|---|---|
| `GET /metadata` | Metadata for authorized tables, views, and functions |
| `GET /`, `/conformance`, `/collections` | OGC discovery and collection descriptions |
| `GET /collections/{schema}.{table}/items` | Query rows or features |
| `GET /collections/{schema}.{table}/items/{id}` | Get one row by primary key |
| `GET /collections/{schema}.{table}/queryables` | Queryable column schema |
| `POST /collections/{schema}.{table}/analysis/{operation}` | Read-only spatial analysis with the caller's row rules |
| `GET /analysis/operations` | List all 15 available analyses |
| `GET /api`, `/swagger/` | Dynamic OpenAPI and Swagger UI |
| `GET /functions` | List authorized functions |
| `GET /functions/{schema}.{name}/items` | Execute a STABLE/IMMUTABLE function |
| `POST /functions/{schema}.{name}/items` | Execute a function; VOLATILE requires opt-in and separate permission |
| `GET /health/live` | Application liveness check |
| `GET /health/ready` | Application and database readiness check |

See the [full documentation](DOCS.md) for all parameters, the authorization model, examples, and architecture.

## Testing

```bash
dotnet test Tests/PgDataHub.Tests.csproj
node --test Tests/admin-i18n.test.cjs
pwsh -File Tests/e2e.ps1 -CollectionPattern public.osm*
```

Set `ADMIN_API_KEY` and optionally `BASE_URL` for the end-to-end script, and choose a collection pattern that exists in the target database. Use PowerShell 7. Production admin sessions require HTTPS; `-SkipCertificateCheck` is available for a local development HTTPS certificate. Integration tests use the configured local PostGIS database and remove their temporary schemas; the trigger-permission fixture also creates a temporary database and role, requiring a privileged test account.

For load testing, install [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) and run:

```bash
k6 run -e BASE_URL=http://localhost:5270 -e API_KEY=your-key -e VUS=100 Tests/performance-smoke.js
```

## License

MIT
