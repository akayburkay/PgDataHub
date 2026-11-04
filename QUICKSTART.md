# PgDataHub Quick Start

Turn PostgreSQL tables, views, and functions into a REST API. Query JSON or GeoJSON, filter records, and control access with roles and API keys. Vector tiles are not included.

## Install

Requirements: Docker with Compose.

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
cp .env.example .env
```

On PowerShell, use `Copy-Item .env.example .env`. Replace both example secrets in `.env` with strong random values, then run:

```bash
docker compose up -d --build
docker compose ps
```

Open http://localhost:5270/admin/ and sign in with your `PGDATAHUB_ADMIN_KEY`. The bundled database starts separately from any existing database you already have; it does not automatically import your tables.

## Give a client access

1. In Configuration, select the collections to expose.
2. In Roles & access, create a role and select its allowed collections. Leave denied collections empty unless you need exceptions. Deny takes precedence.
3. Configure hidden columns if needed.
4. Create an API key assigned to that role. Copy it immediately; the raw key is shown only once.

Client API keys call the data API. Management endpoints require an admin session; sending an API key directly to `/admin/config` is not sufficient.

## Query data

Replace `YOUR_CLIENT_KEY` and `public.roads` with your key and collection ID from metadata. On Windows PowerShell use `curl.exe`.

```bash
curl -H "X-API-Key: YOUR_CLIENT_KEY" http://localhost:5270/metadata
curl -H "X-API-Key: YOUR_CLIENT_KEY" "http://localhost:5270/collections/public.roads/items?limit=10"
curl -H "X-API-Key: YOUR_CLIENT_KEY" "http://localhost:5270/collections/public.roads/items?select=id,name&orderby=name&limit=20&offset=20"
curl -H "X-API-Key: YOUR_CLIENT_KEY" http://localhost:5270/collections/public.roads/queryables
curl -H "X-API-Key: YOUR_CLIENT_KEY" http://localhost:5270/functions
```

Spatial collections support `bbox=minx,miny,maxx,maxy` and return GeoJSON when geometry is selected. Properties can be filtered with `name=Main*` or `id=42`. Use the actual column names shown in queryables.

In Postman: select GET, paste the URL, and add an `X-API-Key` header. No request body is needed.

## Common responses

| Status | Meaning |
|---|---|
| 400 | Invalid parameter or request |
| 401 | Missing/invalid credential or admin session |
| 403 | Your role does not allow this resource |
| 429 | Rate or concurrency limit reached; retry with backoff |

Check readiness at `/health/ready`. Use `docker compose logs -f pgdatahub` for diagnostics. Keep credentials out of URLs and Git. For external access, deploy behind a correctly configured HTTPS reverse proxy.

See [DOCKER.md](DOCKER.md) for operations and [DOCS.md](DOCS.md) for the API reference.
