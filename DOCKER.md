# Docker Installation

This setup starts PgDataHub and PostGIS together. Application configuration and PostgreSQL data are persisted in named volumes.

## Requirements

- Windows/macOS: a current Docker Desktop installation
- Linux: Docker Engine and Docker Compose v2
- At least 2 GB of available memory and approximately 2 GB of disk space for images

```bash
docker version
docker compose version
```

## First Installation

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
cp .env.example .env
```

On Windows PowerShell, use `Copy-Item .env.example .env` instead.

Set unique, strong values for at least these entries in `.env`:

```dotenv
PGDATAHUB_DB_PASSWORD=a-long-random-database-password
PGDATAHUB_ADMIN_KEY=a-long-random-admin-key-at-least-16-characters
```

Start the stack:

```bash
docker compose up -d --build
docker compose ps
```

When both services report `healthy`, open <http://localhost:5270/admin/> and sign in with the `PGDATAHUB_ADMIN_KEY` value.

## Verify the Installation

```bash
curl http://localhost:5270/health/live
curl http://localhost:5270/health/ready
curl -H "X-API-Key: YOUR_ADMIN_KEY" http://localhost:5270/metadata
```

The health endpoints should report `ok` and `ready`. `/metadata` should return `401` without credentials and `200` with a valid admin key.

```bash
docker compose logs -f pgdatahub
docker compose logs -f db
```

## Configuration and Persistence

- PostgreSQL data is stored in the `pgdatahub_postgres` volume.
- Application configuration is stored in the `pgdatahub_config` volume.
- On first startup, a secure template is copied into the configuration volume.
- Database connection values are applied from the Compose environment.
- Only the SHA-256 hash of the admin key is stored; the raw key is never written to configuration.
- Changing the admin key in `.env` and recreating the service safely rotates the Docker-managed key.
- Administration console changes are persisted and applied immediately.

PgDataHub is published only on `127.0.0.1:5270` by default. Change `PGDATAHUB_PORT` in `.env` and run `docker compose up -d` to use another port.

## Use an Existing PostgreSQL Server

The default Compose file creates its own PostGIS service. For an external database, set the `PGDATAHUB_DB_*` values to the target server and use a Compose override that removes `depends_on: db`. Grant the database user only the minimum permissions required for the schemas and objects being exposed.

## Upgrade

```bash
git pull
docker compose up -d --build
docker compose ps
```

Named volumes are preserved. Take a backup before production upgrades.

## Backup

Create a PostgreSQL dump:

```bash
docker compose exec -T db pg_dump -U pgdatahub -d pgdatahub -Fc > pgdatahub.dump
```

Export application configuration:

```bash
docker compose exec -T pgdatahub sh -c "cat /data/pgdatahub.ini" > pgdatahub.docker.ini
```

Backups may contain database data and API key hashes. Store them securely.

## Stop or Remove the Stack

Stop services while preserving data:

```bash
docker compose down
```

`docker compose down -v` permanently deletes both PostgreSQL and configuration volumes. Use it only when intentionally erasing all Docker-managed data.

## Production Security Checklist

- Replace every example value in `.env`, and never commit that file.
- Use a TLS-terminating reverse proxy such as Caddy, Traefik, or Nginx.
- Do not publish the database port; the default Compose file does not expose it.
- Restrict the administration console to a trusted network or VPN.
- Give non-admin clients separate API keys and least-privilege roles.
- Rotate API keys periodically. Raw keys are displayed only once when created.

## Troubleshooting

If the database is unhealthy in `docker compose ps`, inspect `docker compose logs db`. Changing `.env` does not retroactively change PostgreSQL credentials inside an already initialized volume.

If Docker Desktop cannot start or reports local socket errors involving paths such as `engine.sock` or `dockerInference`, the failure occurs before Compose or PgDataHub runs. Repair or reset Docker Desktop, or use its official diagnostics. On a healthy Docker installation, this project starts with `docker compose up -d` and requires no additional local dependency.
