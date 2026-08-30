FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PgDataHub.csproj ./
RUN dotnet restore PgDataHub.csproj
COPY . .
RUN dotnet publish PgDataHub.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY docker/pgdatahub.ini /app/defaults/pgdatahub.ini
COPY docker/entrypoint.sh /usr/local/bin/pgdatahub-entrypoint
RUN chmod +x /usr/local/bin/pgdatahub-entrypoint \
    && mkdir -p /data \
    && chown -R app:app /app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    PGDATAHUB_CONFIG_PATH=/data/pgdatahub.ini
EXPOSE 8080
ENTRYPOINT ["pgdatahub-entrypoint"]
