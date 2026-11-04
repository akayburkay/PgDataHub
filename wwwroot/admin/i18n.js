/* English message IDs are the English catalogue. Only annotated UI text is translated. */
(() => {
  'use strict';
  const tr = Object.freeze({
    'PgDataHub Control': 'PgDataHub Yönetimi',
    'Language': 'Dil',
    'Interface language': 'Arayüz dili',
    'Secure management': 'Güvenli yönetim',
    'Welcome back': 'Hoş geldiniz',
    'Enter the admin key configured as': 'Şu değişkende tanımlanan yönetici anahtarını girin:',
    '. Regular client API keys cannot open this console.': '. Normal istemci API anahtarları bu paneli açamaz.',
    'Administrator API key': 'Yönetici API anahtarı',
    'Paste your admin key': 'Yönetici anahtarınızı yapıştırın',
    'Enter your administrator API key.': 'Yönetici API anahtarınızı girin.',
    'Open management console': 'Yönetim panelini aç',
    'The key is sent only to this PgDataHub instance and is never stored by the browser.': 'Anahtar yalnızca bu PgDataHub sunucusuna gönderilir ve tarayıcıda saklanmaz.',
    'Control center': 'Yönetim merkezi',
    '◫ Overview': '◫ Genel bakış',
    '⚙ Configuration': '⚙ Yapılandırma',
    '♙ Roles & access': '♙ Roller ve erişim',
    '⌁ API keys': '⌁ API anahtarları',
    'Sign out': 'Oturumu kapat',
    'Overview': 'Genel bakış',
    'Configuration': 'Yapılandırma',
    'Roles & access': 'Roller ve erişim',
    'Runtime and metadata at a glance.': 'Çalışma durumu ve metaverilere genel bakış.',
    'Every pgdatahub.ini runtime setting in one place.': 'Tüm pgdatahub.ini çalışma ayarları tek yerde.',
    'Least-privilege collection, function and column policies.': 'Koleksiyon, fonksiyon, kolon ve satır erişim kuralları.',
    'Issue and revoke application credentials.': 'Uygulama anahtarları oluşturun ve iptal edin.',
    'Local bootstrap': 'Oturum bekleniyor',
    'Secure admin session': 'Güvenli yönetici oturumu',
    'Configuration saved. Restart PgDataHub before relying on database or authentication pipeline changes.': 'Yapılandırma kaydedildi. Yeniden başlatma gerektiren değişiklikler için PgDataHub hizmetini yeniden başlatın.',
    'Available collections': 'Kullanılabilir koleksiyonlar',
    'Available functions': 'Kullanılabilir fonksiyonlar',
    'Access roles': 'Erişim rolleri',
    'Quick start': 'Hızlı başlangıç',
    'What would you like to manage?': 'Neyi yönetmek istersiniz?',
    '1. Choose exposed data': '1. Yayınlanacak verileri seçin',
    'Select tables, views, functions, limits, and hidden columns.': 'Tabloları, görünümleri, fonksiyonları, sınırları ve gizli kolonları seçin.',
    '2. Define access': '2. Erişimi tanımlayın',
    'Create roles and decide exactly what each client can read.': 'Roller oluşturun ve her istemcinin okuyabileceği verileri belirleyin.',
    '3. Issue an API key': '3. API anahtarı oluşturun',
    'Create a client credential and assign one role to it.': 'Bir istemci anahtarı oluşturup bir rol atayın.',
    'Security status': 'Güvenlik durumu',
    'Loading…': 'Yükleniyor…',
    'Live metadata': 'Güncel metaveriler',
    'Checking listener…': 'Dinleyici kontrol ediliyor…',
    'Tables, views, and functions are refreshed automatically through notifications and periodic polling.': 'Tablo, görünüm ve fonksiyon metaverileri bildirimler ve periyodik yoklama ile otomatik yenilenir.',
    'Refresh metadata now': 'Metaverileri şimdi yenile',
    'Metadata refreshed': 'Metaveriler yenilendi',
    'Event trigger installed; periodic polling also enabled.': 'Olay tetikleyicisi kurulu; periyodik yoklama da etkin.',
    'Event trigger unavailable; periodic polling enabled.': 'Olay tetikleyicisi kullanılamıyor; periyodik yoklama etkin.',
    'Authentication required': 'Kimlik doğrulama zorunlu',
    'Authentication optional': 'Kimlik doğrulama isteğe bağlı',
    'Connected to': 'Bağlı sunucu',
    'Data exposure': 'Veri yayını',
    'Column visibility': 'Kolon görünürlüğü',
    'Authentication': 'Kimlik doğrulama',
    'Database & performance': 'Veritabanı ve performans',
    'Tables and views': 'Tablolar ve görünümler',
    'Collection exposure': 'Koleksiyon yayını',
    'Use the search buttons to select discovered database objects, or enter glob patterns such as': 'Bulunan veritabanı nesnelerini arama düğmeleriyle seçin veya şu örnekteki gibi kalıplar girin:',
    'Include': 'Dahil et',
    'Only matching collections can appear in metadata and API results.': 'Metaverilerde ve API sonuçlarında yalnızca eşleşen koleksiyonlar görünür.',
    'Exclude': 'Hariç tut',
    'Exclude always wins over include.': 'Hariç tutma kuralları her zaman önceliklidir.',
    'Default rows per request': 'İstek başına varsayılan satır sayısı',
    'Maximum rows per request': 'İstek başına en fazla satır sayısı',
    'PostgreSQL routines': 'PostgreSQL fonksiyonları',
    'Function exposure': 'Fonksiyon yayını',
    'Overloaded functions remain distinct by signature and OID.': 'Aynı adlı fonksiyonlar imza ve OID ile ayrı tutulur.',
    'Volatile function opt-in': 'VOLATILE fonksiyon yayını',
    'Empty by default': 'Varsayılan olarak boş',
    'Requires POST, authentication and separate role permission.': 'POST, kimlik doğrulama ve ayrı rol izni gerektirir.',
    'Execution timeout (seconds)': 'Çalıştırma zaman aşımı (saniye)',
    'Maximum returned rows': 'Döndürülecek en fazla satır sayısı',
    'Response shaping': 'Yanıt düzeni',
    'Collection column rules': 'Koleksiyon kolon kuralları',
    'Choose a collection, then select columns to allow or hide. Hidden columns always win.': 'Bir koleksiyon seçip izin verilecek veya gizlenecek kolonları belirleyin. Gizleme kuralları önceliklidir.',
    '+ Add column rule': '+ Kolon kuralı ekle',
    'Access control': 'Erişim denetimi',
    'Require authentication for API requests': 'API isteklerinde kimlik doğrulama iste',
    'Anonymous/default role': 'Anonim/varsayılan rol',
    'Leave empty to return 401': '401 döndürmek için boş bırakın',
    'Applied only when a request has no credential.': 'Yalnızca kimlik bilgisi içermeyen isteklere uygulanır.',
    'Administrator role': 'Yönetici rolü',
    'Required to access this management console.': 'Bu yönetim paneline erişmek için gereklidir.',
    'Accept API keys': 'API anahtarlarını kabul et',
    'API key header': 'API anahtarı başlığı',
    'Identity provider': 'Kimlik sağlayıcı',
    'JWT bearer tokens': 'JWT erişim belirteçleri',
    'Accept JWT bearer tokens': 'JWT erişim belirteçlerini kabul et',
    'Authority URL': 'Kimlik sağlayıcı adresi',
    'Audience': 'Hedef kitle (audience)',
    'Role claim': 'Rol claim alanı',
    'Connection': 'Bağlantı',
    'Database and connection pool': 'Veritabanı ve bağlantı havuzu',
    'The password is never returned. Leave it blank to keep the current value. Connection changes are applied when saved.': 'Parola yanıtlarda gönderilmez. Mevcut parolayı korumak için boş bırakın. Bağlantı değişiklikleri kaydedildiğinde uygulanır.',
    'Host': 'Sunucu',
    'Port': 'Port',
    'Database': 'Veritabanı',
    'User': 'Kullanıcı',
    'Password': 'Parola',
    'Keep existing password': 'Mevcut parolayı koru',
    'Configured — leave blank to keep': 'Tanımlı — korumak için boş bırakın',
    'Set database password': 'Veritabanı parolasını girin',
    'Minimum pool size': 'En az havuz bağlantısı',
    'Maximum pool size': 'En fazla havuz bağlantısı',
    'Command timeout (seconds)': 'Sorgu zaman aşımı (saniye)',
    'Discard changes': 'Değişiklikleri geri al',
    'Save configuration': 'Yapılandırmayı kaydet',
    'Saved and applied immediately.': 'Kaydedildi ve hemen uygulandı.',
    'Roles': 'Roller',
    'Deny rules override allow rules.': 'Engelleme kuralları izin kurallarından önceliklidir.',
    '+ New role': '+ Yeni rol',
    'Role': 'Rol',
    'Collections': 'Koleksiyonlar',
    'BASE TABLE': 'Tablo',
    'VIEW': 'Görünüm',
    'MATERIALIZED VIEW': 'Materyalize görünüm',
    'Functions': 'Fonksiyonlar',
    'Column rules': 'Kolon kuralları',
    'API keys': 'API anahtarları',
    'Raw secrets are shown exactly once.': 'Anahtarın kendisi yalnızca bir kez gösterilir.',
    '+ Create key': '+ Anahtar oluştur',
    'Name': 'Ad',
    'Owner': 'Sahip',
    'Status': 'Durum',
    'Expires': 'Geçerlilik sonu',
    'Last used': 'Son kullanım',
    'Access policy': 'Erişim politikası',
    'Create role': 'Rol oluştur',
    'Edit role': 'Rolü düzenle',
    'Close': 'Kapat',
    'Start with what this role may access. Deny rules always override allow rules.': 'Bu rolün erişebileceği verileri belirleyin. Engelleme kuralları her zaman önceliklidir.',
    'Role name': 'Rol adı',
    'e.g. map-reader': 'ör. harita-okuyucu',
    'Description': 'Açıklama',
    'Who should use this role?': 'Bu rolü kim kullanacak?',
    'Allowed collections': 'İzin verilen koleksiyonlar',
    'Select collections or use public.*': 'Koleksiyon seçin veya public.* kullanın',
    'Denied collections': 'Engellenen koleksiyonlar',
    'Optional': 'İsteğe bağlı',
    'Allowed functions': 'İzin verilen fonksiyonlar',
    'Denied functions': 'Engellenen fonksiyonlar',
    'Allowed volatile POST functions': 'İzin verilen VOLATILE POST fonksiyonları',
    'Explicit write permission': 'Açık yazma izni',
    'Denied volatile POST functions': 'Engellenen VOLATILE POST fonksiyonları',
    'Column visibility rules': 'Kolon görünürlük kuralları',
    'Add rules visually; no JSON editing is required.': 'Kuralları formdan ekleyin; JSON düzenlemeniz gerekmez.',
    'Row access rules': 'Satır erişim kuralları',
    "Conditions within a role are AND. Access granted by multiple roles is OR; an unrestricted role grants all rows. Missing JWT claims deny that role's rows.": 'Bir rolün koşulları VE ile, farklı rollerin izinleri VEYA ile birleşir. Kısıtlanmamış bir rol tüm satırlara erişir. JWT claim alanı eksikse o rol satır izni vermez.',
    '+ Add row rule': '+ Satır kuralı ekle',
    'Cancel': 'Vazgeç',
    'Save role': 'Rolü kaydet',
    'Create API key': 'API anahtarı oluştur',
    'Expires at': 'Geçerlilik bitişi',
    'Create key': 'Anahtar oluştur',
    'Copy this key now': 'Anahtarı şimdi kopyalayın',
    'It cannot be retrieved again.': 'Bu anahtar tekrar görüntülenemez.',
    'Copy': 'Kopyala',
    'Done': 'Tamam',
    'Select': 'Seçin',
    'Search schema, table, function or column…': 'Şema, tablo, fonksiyon veya kolon ara…',
    'Select function': 'Fonksiyon seçin',
    'Select columns': 'Kolon seçin',
    'Select collection': 'Koleksiyon seçin',
    '{count} selected': '{count} seçildi',
    'No matches.': 'Eşleşme bulunamadı.',
    'None': 'Yok',
    'No collection-specific overrides.': 'Koleksiyona özel kural yok.',
    'schema.collection': 'şema.koleksiyon',
    'Allow columns': 'İzin verilen kolonlar',
    'Hide columns': 'Gizlenecek kolonlar',
    'Allowed columns': 'İzin verilen kolonlar',
    'Hidden columns': 'Gizli kolonlar',
    'Collection pattern': 'Koleksiyon kalıbı',
    'Remove': 'Kaldır',
    'Remove rule': 'Kuralı kaldır',
    '+ Add AND condition': '+ VE koşulu ekle',
    'Column': 'Kolon',
    'Operator': 'Operatör',
    'Value source': 'Değer kaynağı',
    'Fixed value': 'Sabit değer',
    'JWT claim': 'JWT claim alanı',
    'Value': 'Değer',
    'e.g. 42 or tenant_id': 'ör. 42 veya tenant_id',
    'Remove condition': 'Koşulu kaldır',
    'JWT claim name': 'JWT claim alanının adı',
    'Values (comma separated)': 'Değerler (virgülle ayrılmış)',
    'Equal (=)': 'Eşit (=)',
    'Not equal (≠)': 'Eşit değil (≠)',
    'Greater than (>)': 'Büyük (>)',
    'Greater or equal (≥)': 'Büyük veya eşit (≥)',
    'Less than (<)': 'Küçük (<)',
    'Less or equal (≤)': 'Küçük veya eşit (≤)',
    'In list': 'Listede',
    'Not in list': 'Listede değil',
    'Between': 'Arasında',
    'Is null': 'Boş (null)',
    'Is not null': 'Boş değil (null değil)',
    'Denied: {count}': 'Engellenen: {count}',
    'Volatile POST': 'VOLATILE POST',
    '{count} column rules': '{count} kolon kuralı',
    '{count} row rules': '{count} satır kuralı',
    '{count} columns': '{count} kolon',
    'Edit': 'Düzenle',
    'Delete': 'Sil',
    'No roles.': 'Rol yok.',
    'Role name is required.': 'Rol adı gereklidir.',
    'Role saved': 'Rol kaydedildi',
    'Role deleted': 'Rol silindi',
    'Delete role "{name}"?': '"{name}" rolü silinsin mi?',
    'Active': 'Etkin',
    'Disabled': 'Devre dışı',
    'Never': 'Hiçbir zaman',
    'Revoke': 'İptal et',
    'No API keys.': 'API anahtarı yok.',
    'API key status updated': 'API anahtarı durumu güncellendi',
    'API key revoked': 'API anahtarı iptal edildi',
    'Revoke API key #{id}?': '#{id} API anahtarı iptal edilsin mi?',
    'Copied to clipboard': 'Panoya kopyalandı',
    'Could not copy the key. Select and copy it manually.': 'Anahtar kopyalanamadı. Seçip elle kopyalayın.',
    'Cannot reach the server.': 'Sunucuya ulaşılamıyor.',
    'Invalid administrator credential.': 'Yönetici anahtarı geçersiz.',
    'Admin login required.': 'Yönetici oturumu açmanız gerekiyor.',
    'Admin session or CSRF token is invalid.': 'Yönetici oturumu veya CSRF belirteci geçersiz. Yeniden oturum açın.',
    'Forbidden': 'Erişim engellendi',
    'Role not found.': 'Rol bulunamadı.',
    'Role is still referenced by one or more API keys.': 'Bu rolü kullanan API anahtarları var. Önce bu anahtarları iptal edin veya rollerini değiştirin.',
    'API key not found.': 'API anahtarı bulunamadı.',
    'Role names may contain only letters, numbers, \'.\', \'_\' and \'-\'.': 'Rol adı yalnızca harf, rakam, nokta, alt çizgi ve tire içerebilir.',
    'At most 64 row rules are allowed per role.': 'Bir role en fazla 64 satır kuralı eklenebilir.',
    'Every row rule needs a collection pattern and 1–16 conditions.': 'Her satır kuralı bir koleksiyon kalıbı ve 1–16 koşul içermelidir.',
    'Invalid row condition column, operator or values.': 'Satır koşulunun kolonu, operatörü veya değerleri geçersiz.',
    'Claim conditions use eq/in and cannot also have literal values.': 'Claim koşullarında eşitlik/liste operatörü kullanın; ayrıca sabit değer girmeyin.',
    'The row condition has the wrong number of values.': 'Satır koşulundaki değer sayısı operatöre uygun değil.',
    'database.minPoolSize cannot exceed maxPoolSize.': 'En az havuz bağlantısı, en fazla bağlantı sayısını aşamaz.',
    'feature.defaultLimit cannot exceed maxLimit.': 'Varsayılan satır sınırı en yüksek sınırı aşamaz.',
    'auth.jwt.authority must be an absolute URL.': 'JWT kimlik sağlayıcı adresi tam bir URL olmalıdır.',
    'Configuration values cannot contain line breaks.': 'Yapılandırma değerleri satır sonu içeremez.',
    '{field} is required.': '{field} gereklidir.',
    '{field} must be between {min} and {max}.': '{field}, {min} ile {max} arasında olmalıdır.',
    '{field} cannot contain line breaks.': '{field} satır sonu içeremez.',
    "Unknown role '{name}'.": "'{name}' rolü bulunamadı.",
    'Duplicate table override: {name}.': 'Aynı koleksiyon için birden fazla kural var: {name}.',
    'Some values are invalid. Check the form and try again.': 'Bazı değerler geçersiz. Formu kontrol edip yeniden deneyin.',
    'Your session has expired. Sign in again.': 'Oturumunuz sona erdi. Yeniden oturum açın.',
    'You do not have permission for this action.': 'Bu işlem için izniniz yok.',
    'The requested resource was not found.': 'İstenen kaynak bulunamadı.',
    'Too many requests. Try again later.': 'Çok fazla istek gönderildi. Biraz sonra yeniden deneyin.',
    'The request could not be completed.': 'İstek tamamlanamadı.'
  });
  const patterns = [
    [/^(.+) is required\.$/, '{field} is required.', ['field']],
    [/^(.+) must be between (\d+) and (\d+)\.$/, '{field} must be between {min} and {max}.', ['field', 'min', 'max']],
    [/^(.+) cannot contain line breaks\.$/, '{field} cannot contain line breaks.', ['field']],
    [/^Unknown role '(.+)'\.$/, "Unknown role '{name}'.", ['name']],
    [/^Duplicate table override: (.+)\.$/, 'Duplicate table override: {name}.', ['name']]
  ];
  const storageKey = 'pgdh_language';
  const singulars = { '{count} columns': '{count} column', '{count} column rules': '{count} column rule', '{count} row rules': '{count} row rule' };
  const supported = value => ['tr', 'en'].includes(value);
  let saved;
  try { saved = localStorage.getItem(storageKey); } catch { /* Private/storage-restricted browsers remain usable. */ }
  let language = supported(saved) ? saved : (navigator.languages || [navigator.language || 'en'])
    .map(value => value.toLowerCase().split('-')[0]).find(supported) || 'en';
  const formats = Object.fromEntries(['tr', 'en'].map(lang => [lang, {
    number: new Intl.NumberFormat(lang === 'tr' ? 'tr-TR' : 'en-US'),
    date: new Intl.DateTimeFormat(lang === 'tr' ? 'tr-TR' : 'en-US', { dateStyle: 'medium', timeStyle: 'short' })
  }]));
  const number = value => formats[language].number.format(value);
  function messageParts(message) {
    if (Object.hasOwn(tr, message)) return { key: message, params: {} };
    for (const [pattern, key, names] of patterns) {
      const match = pattern.exec(message);
      if (match) return { key, params: Object.fromEntries(names.map((name, i) => [name, match[i + 1]])) };
    }
    return { key: message, params: {} };
  }
  function t(message, params = {}) {
    const parsed = messageParts(String(message));
    const values = { ...parsed.params, ...params };
    const text = language === 'tr' ? Object.hasOwn(tr, parsed.key) ? tr[parsed.key] : parsed.key
      : values.count === 1 && Object.hasOwn(singulars, parsed.key) ? singulars[parsed.key] : parsed.key;
    return text.replace(/\{(\w+)\}/g, (token, key) => Object.hasOwn(values, key)
      ? typeof values[key] === 'number' ? number(values[key]) : String(values[key]) : token);
  }
  function setText(element, message, params = {}) {
    element.dataset.i18n = message;
    element.dataset.i18nParams = JSON.stringify(params);
    element.textContent = t(message, params);
  }
  function formatDate(value) {
    if (!value) return t('Never');
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : formats[language].date.format(date);
  }
  function translate(root = document) {
    root.querySelectorAll('[data-i18n]').forEach(element => {
      element.textContent = t(element.dataset.i18n, JSON.parse(element.dataset.i18nParams || '{}'));
    });
    root.querySelectorAll('[data-i18n-placeholder]').forEach(element => element.placeholder = t(element.dataset.i18nPlaceholder));
    root.querySelectorAll('[data-i18n-aria-label]').forEach(element => element.setAttribute('aria-label', t(element.dataset.i18nAriaLabel)));
    root.querySelectorAll('[data-i18n-date]').forEach(element => element.textContent = formatDate(element.dataset.i18nDate));
    root.querySelectorAll('[data-language-select]').forEach(select => select.value = language);
  }
  function setLanguage(value) {
    if (!supported(value)) return;
    language = value;
    try { localStorage.setItem(storageKey, value); } catch { /* Keep the choice for this page. */ }
    document.documentElement.lang = value;
    translate();
    window.dispatchEvent(new Event('pgdh:languagechange'));
  }
  function responseError(data, status) {
    const message = data?.error || data?.detail || data?.title;
    if (typeof message === 'string' && Object.hasOwn(tr, messageParts(message).key)) return message;
    return ({ 400: 'Some values are invalid. Check the form and try again.', 401: 'Your session has expired. Sign in again.',
      403: 'You do not have permission for this action.', 404: 'The requested resource was not found.',
      429: 'Too many requests. Try again later.' })[status] || 'The request could not be completed.';
  }
  document.documentElement.lang = language;
  window.PgDataHubI18n = Object.freeze({ t, setText, translate, setLanguage, formatDate, responseError, number,
    get language() { return language; }, messages: tr });
})();
