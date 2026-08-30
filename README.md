# PgDataHub

PostgreSQL tablolarını, view'larını ve fonksiyonlarını kod yazmadan güvenli bir REST API'ye dönüştüren .NET 10 servisi. PostGIS/GeoJSON, canlı metadata, API key/JWT yetkilendirme ve yerleşik yönetim arayüzü içerir.

## Öne çıkan özellikler

- **Sıfır uygulama kodu:** `pgdatahub.ini` veya yönetim panelinden yapılandırılır.
- **PostGIS ve GeoJSON:** Geometry kolonları otomatik `FeatureCollection` olarak sunulur; bbox ve CRS dönüşümü desteklenir.
- **Güçlü sorgulama:** Filtre, sıralama, kolon seçimi, sayfalama, distinct ve count.
- **Canlı metadata:** Uygun şema değişiklikleri PostgreSQL `LISTEN/NOTIFY` ile restart gerektirmeden alınır.
- **Güvenlik:** API key ve JWT, rol bazlı tablo/view/fonksiyon ve kolon yetkileri, güvenli admin oturumu ve CSRF koruması.
- **Fonksiyonlar:** Parametreli ve overload PostgreSQL fonksiyonları OID/signature ile doğru ayrılır.
- **Performans:** Metadata cache, bağlantı havuzu, streaming JSON, Brotli/Gzip ve isteğe bağlı `Server-Timing` ölçümleri.

## Hızlı kurulum

### Docker Compose (önerilen)

Gereksinim: Docker Desktop (Windows/macOS) veya Docker Engine + Compose v2 (Linux).

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
cp .env.example .env
# .env içindeki iki replace-with-... değerini değiştirin
docker compose up -d
```

Windows PowerShell'de kopyalama komutu: `Copy-Item .env.example .env`.

Kurulum tamamlandığında:

- Yönetim paneli: <http://localhost:5270/admin/>
- Hazır olma kontrolü: <http://localhost:5270/health/ready>

Yönetim paneline `.env` dosyasındaki `PGDATAHUB_ADMIN_KEY` ile giriş yapın. Compose; PgDataHub ve PostGIS'i kurar, veritabanı hazır olana kadar bekler, verileri named volume'larda saklar ve servisleri otomatik yeniden başlatır.

```bash
docker compose ps
docker compose logs -f pgdatahub
```

İlk doğrulama isteği:

```bash
curl -H "X-API-Key: BURAYA_ADMIN_KEY" http://localhost:5270/metadata
```

> Varsayılan örnek parolaları kullanmayın. Uygulama Compose ile yalnızca `127.0.0.1` üzerinde yayınlanır; internete açarken TLS kullanan bir reverse proxy ve ağ erişim kuralları ekleyin.

Ayrıntılı kurulum, güncelleme, yedekleme ve sorun giderme için [Docker kurulum rehberine](DOCKER.md) bakın.

### Yerel .NET

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
# pgdatahub.ini içindeki PostgreSQL bağlantısını düzenleyin
dotnet run
```

Gereksinimler: .NET 10 SDK ve erişilebilir PostgreSQL 14+ (geometry için PostGIS).

## Temel yapılandırma (`pgdatahub.ini`)

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

Yönetim panelinden kaydedilen auth/config değişiklikleri dosyaya atomik olarak yazılır ve restart gerektirmeden uygulanır. Docker kurulumunda config, `pgdatahub_config` volume'unda tutulur.

## API özeti

| Endpoint | Açıklama |
|---|---|
| `GET /metadata` | Yetkili tablo, view ve fonksiyon metadata'sı |
| `GET /collections/{schema}.{table}/items` | Kayıt/feature sorgulama |
| `GET /collections/{schema}.{table}/items/{id}` | Primary key ile tek kayıt |
| `GET /collections/{schema}.{table}/queryables` | Sorgulanabilir kolon şeması |
| `GET /functions` | Yetkili fonksiyon listesi |
| `GET /functions/{schema}.{name}/items` | Fonksiyon çalıştırma |
| `GET /health/live` | Uygulama yaşam kontrolü |
| `GET /health/ready` | Uygulama + veritabanı hazır olma kontrolü |

Tüm parametreler, auth modeli, örnek istekler ve mimari için [genel dokümantasyona](DOCS.md) bakın.

## Test

```bash
dotnet run --project Tests/PgDataHub.Tests.csproj
powershell -ExecutionPolicy Bypass -File Tests/e2e.ps1
```

Yük testi için [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) kurulduktan sonra:

```bash
k6 run -e BASE_URL=http://localhost:5270 -e API_KEY=your-key -e VUS=100 Tests/performance-smoke.js
```

## Lisans

MIT
