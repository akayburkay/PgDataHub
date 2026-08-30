# Docker ile kurulum

Bu kurulum PgDataHub ile PostGIS'i birlikte ayağa kaldırır. Uygulama config'i ve PostgreSQL verisi named volume'larda kalıcıdır.

## Gereksinimler

- Windows/macOS: güncel Docker Desktop
- Linux: Docker Engine ve Docker Compose v2
- En az 2 GB boş RAM ve image'lar için yaklaşık 2 GB disk

Doğrulama:

```bash
docker version
docker compose version
```

## İlk kurulum

```bash
git clone https://github.com/akayburkay/PgDataHub.git
cd PgDataHub
cp .env.example .env
```

Windows PowerShell kullanıyorsanız son komut yerine:

```powershell
Copy-Item .env.example .env
```

`.env` dosyasında en az şu iki değeri benzersiz ve güçlü değerlerle değiştirin:

```dotenv
PGDATAHUB_DB_PASSWORD=uzun-ve-rastgele-bir-veritabani-parolasi
PGDATAHUB_ADMIN_KEY=en-az-16-karakterlik-uzun-rastgele-admin-anahtari
```

Ardından:

```bash
docker compose up -d --build
docker compose ps
```

Her iki servis de `healthy` olduğunda yönetim paneli <http://localhost:5270/admin/> adresindedir. Giriş anahtarı `.env` içindeki `PGDATAHUB_ADMIN_KEY` değeridir.

## Kurulumu doğrulama

```bash
curl http://localhost:5270/health/live
curl http://localhost:5270/health/ready
curl -H "X-API-Key: BURAYA_ADMIN_KEY" http://localhost:5270/metadata
```

Beklenen health yanıtları sırasıyla `ok` ve `ready` durumudur. `/metadata` endpoint'i kimlik bilgisi olmadan `401`, geçerli admin anahtarıyla `200` döner.

Loglar:

```bash
docker compose logs -f pgdatahub
docker compose logs -f db
```

## Yapılandırma ve kalıcılık

- PostgreSQL verisi: `pgdatahub_postgres` volume'u
- Uygulama config'i: `pgdatahub_config` volume'u
- İlk açılışta güvenli şablon config volume'a kopyalanır.
- DB bağlantısı Compose environment değerlerinden uygulanır.
- Admin anahtarının yalnızca SHA-256 hash'i config'e yazılır; açık anahtar saklanmaz.
- `.env` içindeki admin anahtarı değiştirilip servis yeniden oluşturulursa Docker tarafından oluşturulan admin key güvenli biçimde döndürülür.
- Yönetim panelinden yapılan değişiklikler kalıcı config'e yazılır ve anında uygulanır.

Uygulama varsayılan olarak yalnızca `127.0.0.1:5270` üzerinde erişilebilir. Portu değiştirmek için `.env` içindeki `PGDATAHUB_PORT` değerini değiştirip `docker compose up -d` çalıştırın.

## Mevcut PostgreSQL kullanma

Varsayılan Compose kendi PostGIS servisini oluşturur. Harici veritabanı kullanmak için `docker-compose.yml` içindeki `PGDATAHUB_DB_*` environment değerlerini hedef sunucuya göre ayarlayın ve `depends_on: db` bağımlılığını kaldıran ayrı bir Compose override kullanın. Veritabanı kullanıcısına yalnızca yayınlanacak şema ve nesneler için gereken en düşük izinleri verin.

## Güncelleme

```bash
git pull
docker compose up -d --build
docker compose ps
```

Named volume'lar korunduğu için config ve veritabanı verisi kalır. Güncellemeden önce yedek alınması önerilir.

## Yedekleme

PostgreSQL yedeği:

```bash
docker compose exec -T db pg_dump -U pgdatahub -d pgdatahub -Fc > pgdatahub.dump
```

Config'i görüntüleme/yedekleme:

```bash
docker compose exec -T pgdatahub sh -c "cat /data/pgdatahub.ini" > pgdatahub.docker.ini
```

Yedek dosyaları API key hash'leri ve veritabanı içeriği taşıyabileceği için güvenli saklayın.

## Durdurma ve kaldırma

Servisleri durdurmak, verileri korumak için:

```bash
docker compose down
```

`docker compose down -v` PostgreSQL ve config volume'larını da kalıcı olarak siler. Yalnızca verileri gerçekten sıfırlamak istediğinizde kullanın.

## Güvenli yayınlama kontrol listesi

- `.env` örnek değerlerini mutlaka değiştirin ve dosyayı Git'e eklemeyin.
- Dış erişimde HTTPS terminasyonu yapan Caddy, Traefik veya Nginx gibi bir reverse proxy kullanın.
- Veritabanı portunu internete yayınlamayın; mevcut Compose zaten DB portu açmaz.
- Admin panelini güvenilir ağ/VPN ile sınırlayın.
- Admin dışı istemcilere ayrı API key ve en az yetkili rol verin.
- API key'leri periyodik döndürün; ham anahtar yalnızca oluşturulurken gösterilir.

## Sorun giderme

`docker compose ps` içinde DB sağlıksızsa önce `docker compose logs db` çıktısını inceleyin. Eski bir volume farklı kullanıcı/parola ile oluşturulduysa `.env` değişikliği mevcut PostgreSQL kullanıcısını geriye dönük değiştirmez.

Docker Desktop hiç başlayamıyorsa veya `engine.sock`, `dockerInference` gibi yerel socket hataları veriyorsa sorun Compose projesinden önce, bilgisayardaki Docker Desktop kurulumundadır. Docker Desktop'ı onarın/sıfırlayın veya resmi tanılama aracını çalıştırın; sağlıklı bir Docker kurulumunda proje ek bir yerel bağımlılık olmadan `docker compose up -d` ile açılır.
