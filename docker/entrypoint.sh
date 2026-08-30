#!/bin/sh
set -eu

config_path="${PGDATAHUB_CONFIG_PATH:-/data/pgdatahub.ini}"
if [ ! -f "$config_path" ]; then
    mkdir -p "$(dirname "$config_path")"
    cp /app/defaults/pgdatahub.ini "$config_path"
fi

exec dotnet /app/PgDataHub.dll
