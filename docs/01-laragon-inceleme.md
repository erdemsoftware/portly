# Laragon İnceleme Raporu (Portly için temel)

Tarih: 2026-10-22
İncelenen kurulum: `C:\laragon` (Laragon 2026-02-14 sürümü, `laragon.exe` 64-bit + `laragon6.exe` 32-bit eski sürüm)

Amaç: Laragon'un nasıl çalıştığını dosya düzeyinde anlamak, iyi ve kötü yanlarını çıkarmak ve
Portly'nin (açık kaynak, reklamsız, çok dilli, uzaktan güncellemeli, Win7–Win11) tasarımına girdi üretmek.

---

## 1. Laragon nedir, nasıl çalışır (özet)

- Tek bir **tepsi (tray) uygulaması** (`laragon.exe`, ~5,5 MB, Delphi ile yazılmış kapalı kaynak native Win32 exe).
- **Windows servisi kurmaz.** Apache/MySQL/Nginx/Redis vb. hepsi laragon.exe'nin çocuk süreci olarak çalışır;
  Laragon kapanınca hepsi durur. Kayıt defterine (registry) neredeyse hiç dokunmaz → taşınabilir (portable).
- Tüm yazılımlar `bin\<yazılım>\<sürüm-klasörü>` düzeninde **zip'ten açılmış** halde durur; kurulum (installer) yoktur.
- Hangi sürümün aktif olduğu `usr\profile\default.ini` içinde yazar. Sürüm değiştirmek = ini'deki klasör adını değiştirip servisi yeniden başlatmak.
- Çalışma anında yapılandırma dosyalarını **şablondan (template) üretir** (vhost, nginx server bloğu, my.ini, openssl.conf).
- Hız/küçüklük iddiası doğru: çalışan süreçler sadece `laragon.exe` + httpd (2 süreç) + mysqld + php-cgi. Boşta bellek ~10 MB.

---

## 2. Klasör yapısı (gerçek kurulumdan)

```
C:\laragon\
├─ laragon.exe            Ana program (Delphi, kapalı kaynak)
├─ VERSION.txt            [Laragon] Version=20260214000000
├─ EULA.txt               Lisans sözleşmesi (ticari kullanım ücretli!)
├─ bin\                   Tüm yazılım paketleri (her biri zip'ten açılmış, sürüm klasörlü)
│   ├─ apache\httpd-2.4.69-261002-Win64-VS18\     (ApacheLounge build'i)
│   ├─ php\php-8.5.12-nts-Win32-vs17-x64\          (windows.php.net resmi NTS zip)
│   ├─ mysql\mysql-26.7.0-winx64\                  (dev.mysql.com zip)
│   ├─ nginx\nginx-1.31.8\
│   ├─ nodejs\node-v22\
│   ├─ redis\redis-x64-5.0.14.1\
│   ├─ memcached\, mailpit\1.22.3\, python\python-3.13\
│   ├─ composer\ (composer.phar + .bat), git\ (PortableGit), cmder\ (terminal)
│   ├─ heidisql\, notepad++\, code\ (VS Code), ngrok\, cronical\
│   └─ laragon\           Laragon'un kendi yardımcı dosyaları (aşağıda)
├─ etc\                   Laragon'un ÜRETTİĞİ / yönettiği yapılandırmalar
│   ├─ apache2\
│   │   ├─ fcgid.conf           PHP FastCGI (mod_fcgid) ayarı — aktif PHP klasörü gömülü
│   │   ├─ mod_php.conf         mod_php alternatifi (TS PHP için) — şu an kullanılmıyor
│   │   ├─ httpd-ssl.conf       Listen 443 + TLS 1.2/1.3
│   │   ├─ alias\*.conf         /phpmyadmin, /adminer, /redis gibi alias'lar
│   │   ├─ modules\             mod_fcgid .so dosyaları (VS16 ve VS18)
│   │   └─ sites-enabled\       00-default.conf + auto.<proje>.test.conf (otomatik vhost'lar)
│   ├─ nginx\  (alias\, sites-enabled\, php_upstream.conf → 127.0.0.1:10987)
│   ├─ ssl\    (laragon.crt/key/csr, auto.openssl.conf, cacert.pem)
│   ├─ apps\   (phpmyadmin, adminer, memcached, phpRedisAdmin — web araçları)
│   └─ php\pear\
├─ usr\                   KULLANICI ayarları (güncellemede dokunulmayan kısım)
│   ├─ laragon.ini        Ana ayar dosyası
│   ├─ profile\default.ini  Aktif sürümler (apache/php/mysql/nginx/node/…)
│   ├─ packages.conf      İndirilebilir paket kataloğu (ad=URL)
│   ├─ sites.conf         "Quick app" şablonları (WordPress, Laravel, Symfony…)
│   ├─ Procfile           Otomatik başlatılacak özel komutlar
│   ├─ user.cmd           Terminal açılışında çalışan kullanıcı komutları
│   ├─ tpl\               Kullanıcının üzerine yazabildiği şablonlar
│   └─ bin\, share\
├─ data\                  MySQL veri klasörleri (mysql-26.7, mysql-8.4 …)
├─ backup\mysql\          Otomatik SQL yedekleri (8 saatte bir, son 5)
├─ tmp\                   PHP session + error log + indirilen zip'ler (downloads\, extract\)
├─ www\                   Varsayılan DocumentRoot (index.php açılış sayfası)
└─ app\                   (boş)
```

### `bin\laragon\` içeriği (uygulamanın kendi kaynakları)

| Dosya / klasör | Görev |
|---|---|
| `lang\*.txt` (34 dil) | Çeviri dosyaları. Format: `numara = metin`, `\n` satır sonu, `%s` yer tutucu |
| `tpl\*.manifest.tpl` | Fabrika şablonları; `usr\tpl\` içindeki kopyalar öncelikli (manifest = "Laragon'un sahip olduğu, güncellemede üzerine yazılır") |
| `utils\curl.exe`, `openssl\openssl.exe`, `helper.exe`, `SendSignal-x64.exe`, `hosts.js` | İndirme, sertifika üretme, süreçlere Ctrl+C sinyali gönderme, hosts dosyası yazma |
| `laragon.cmd` | Terminal için PATH ve ortam değişkenlerini kuran script |
| `Procfile.bat` | Procfile komutlarını başlatan sarmalayıcı |
| `sites.manifest.conf`, `quotes.manifest.txt`, `README.txt` | Varsayılan içerikler |
| `lib\7z.dll, zlib1.dll, bzip2.dll` | Zip/7z açma |
| `unins000.exe` | Inno Setup kaldırıcı → kurulum Inno Setup ile yapılmış |

**"manifest" kuralı:** `X.manifest.tpl` Laragon'un sahip olduğu dosya; kullanıcı `usr\tpl\X.tpl` olarak kopyalayıp düzenlerse Laragon onu kullanır. Üretilen vhost dosyalarında da `auto.` öneki aynı mantık: "auto." ile başlayanları Laragon silip yeniden yazar, öneki kaldırırsan sana ait olur. **Bu, Portly'ye aynen alınmalı.**

---

## 3. Ana ayar dosyası: `usr\laragon.ini`

```ini
[preferences]
FirstRun=0
RunAtStartup=0
AutoVirtualHosts=-1          ; -1 = açık
Lang=Turkish
DataDir=C:\laragon\data
AutoCheck=0
DocumentRootList=C:\laragon\www|E:\|E:\projeler|   ; önceki document root'lar

[apache]   SSLEnabled=-1  Use=-1  DocumentRoot=E:\projeler
[mysql]    Use=-1
[nginx]    Use=-1  SSLEnabled=0  DocumentRoot=E:\projeler
[mailpit]  Use=-1
[memcached] Use=-1
[redis]    Use=-1
[mongodb]  Use=-1
[postgresql] Use=-1
```

Belgelenmemiş ama bilinen ek anahtarlar: `[nginx] PHP_FCGI_CHILDREN=10`, `[apache] Port=80 SSLPort=443`, `[mysql] Port=3306`, `[preferences] TLD=test`.

---

## 4. Servis yönetimi — her servis nasıl başlatılıyor

| Servis | Başlatma biçimi | Yapılandırma |
|---|---|---|
| **Apache** | `bin\apache\<v>\bin\httpd.exe` çocuk süreç. Başlamadan önce `httpd.conf` içine Laragon kendi satırlarını ekler (ServerRoot, Listen, DocumentRoot, en altta 4 Include) | `IncludeOptional etc/apache2/alias/*.conf`, `IncludeOptional etc/apache2/sites-enabled/*.conf`, `Include httpd-ssl.conf`, `Include fcgid.conf` |
| **PHP (Apache)** | mod_fcgid ile `php-cgi.exe` (NTS build). Alternatif: mod_php (TS build, `php8apache2_4.dll`) | `fcgid.conf` içinde PHP klasörü, PHPRC, FcgidWrapper gömülü. Sürüm değişince dosya yeniden yazılır |
| **PHP (Nginx)** | `php-cgi.exe -b 127.0.0.1:10987` (FastCGI TCP) | `etc\nginx\php_upstream.conf` |
| **MySQL** | `mysqld.exe --defaults-file=<bin>\my.ini` (my.ini şablondan üretilir, `datadir` = `data\mysql-<major.minor>`) | `usr\tpl\MySQL.my.ini.tpl`. Veri klasörü boşsa `mysqld --initialize-insecure` ile root şifresiz başlatır (log'da "Init DataDir" satırı) |
| **Nginx** | `nginx.exe` çocuk süreç; `nginx.conf` şablondan üretilir | sites-enabled/*.conf, 8080 portu (Apache ile çakışmasın diye) |
| **Redis / Memcached / Mailpit** | Doğrudan exe çocuk süreç | Mailpit: `sendmail_path` php.ini'ye yazılır (`mailpit.exe sendmail`) |
| **Durdurma** | `SendSignal-x64.exe` ile Ctrl+C/Ctrl+Break gönderir (düzgün kapanış), olmazsa süreç öldürme | `tmp\netstat-*.log` port çakışma tespiti |

Önemli davranışlar:
- Başlamadan önce **port kontrolü** yapar (80/443/3306); başka WAMP/XAMPP çalışıyorsa uyarır (dil dosyası 713–715).
- "Reload" (Apache'yi yeniden yükle) vhost değişiminden sonra otomatik.
- Laragon kapanırken tüm servisleri durdurur (servis olmadığı için başka yol yok).

---

## 5. Otomatik sanal sunucu (Auto Virtual Hosts) — Laragon'un "sihri"

Akış (her "Yenile"/başlatmada ve DocumentRoot klasörü izlendiğinde):

1. `DocumentRoot` (burada `E:\projeler`) altındaki **her alt klasör** = bir proje. Geçersiz ad (boşluk, Türkçe karakter, ör. "Özel") atlanır (log: *Skip creating auto virtual host for "Özel"*).
2. Her proje için `etc\apache2\sites-enabled\auto.<ad>.test.conf` üretir (şablon: `VirtualHost.SSL.manifest.tpl`). `ServerAlias *.<ad>.test` sayesinde alt alanlar da çalışır. Laravel gibi `public\` klasörü olan projelerde DocumentRoot'u `public`'e çevirir.
3. `C:\Windows\System32\drivers\etc\hosts` dosyasına `127.0.0.1 <ad>.test #laragon magic!` satırı ekler.
   - Yönetici değilse `hosts.js` + node ile UAC yükseltmesi dener; dosya salt okunur / antivirüs engeliyorsa özelliği kapatır (dil 726–730).
   - Dosya `tmp\hosts.tmp` üzerinden yazılır.
4. SSL açıksa `etc\ssl\auto.openssl.conf` içine **her proje için `DNS.n = ad.test` ve `*.ad.test`** ekler, `openssl.exe` ile tek bir `laragon.crt` (self-signed, SAN'lı) üretir ve "Add laragon.crt to Trust Store" ile `certutil` üzerinden kök sertifika deposuna ekler.
5. Apache'yi reload eder.

Zayıf noktaları (Portly'de düzeltilecek):
- Tek sertifika, her projede yeniden üretiliyor → her yeni projede tarayıcı güvenini yeniden kazanmak gerekebiliyor. Doğrusu: **yerel bir kök CA (Portly Root CA)** bir kez güvenilir yapılır, her site için ondan imzalı sertifika üretilir (mkcert mantığı).
- hosts dosyası için node.js'e bağımlı (node yoksa yönetici olarak çalışmak gerekir).
- Proje adı doğrulaması belirsiz (Türkçe karakter sessizce atlanıyor, kullanıcıya bildirim sadece log'da).
- `.test` dışında TLD gizli ayar (`TLD=`), arayüzde yok (yeni sürümde "WildcardDNS" seçeneği var).

---

## 6. Sürüm yönetimi ve paket kataloğu

- `usr\packages.conf`: `Ad=URL` satırları, `---` ile grup ayrımı, `#` yorum, `*` ile işaretli = varsayılan/önerilen. Menüde "Tools > Quick add" listesi buradan oluşur.
- İndirme: `curl.exe` ile `tmp\downloads\<tarih>.<tür>.<dosya>.zip`, açma: `tmp\extract\` → `bin\<tür>\<klasör>`.
- Yeni PHP sürümü eklendiğinde `php.ini-development` kopyalanıp Laragon kendi ayarlarını işler:
  `max_execution_time=36000`, `memory_limit=1G`, `post_max_size=2G`, `upload_max_filesize=2G`,
  `error_log=tmp/php_errors.log`, `session.save_path=tmp`, `extension_dir`, `include_path` (pear),
  `curl.cainfo=etc/ssl/cacert.pem`, `sendmail_path=mailpit`, uzantılar: curl, fileinfo, gd, intl, mbstring, exif, mysqli, openssl, pdo_mysql, pdo_sqlite, sodium, sqlite3, xsl, zip.
  (Bu kurulumda `session.upload_progress.enabled = Off` elle eklenmiş; **Portly'de varsayılan olmalı**.)
- "Auto Update" (sürüm 8): en yeni PHP'yi otomatik indirip ekler; uygulamanın kendisini güncellemez (installer indirtir).
- Menü > PHP > Version ile sürüm değişince: `default.ini` yazılır, `fcgid.conf` (veya `mod_php.conf`) yeniden üretilir, `laragon.cmd` PATH'i yenilenir, Apache yeniden başlar.
- MySQL sürüm değişince veri klasörü de ayrılır (`data\mysql-8.4` ↔ `data\mysql-26.7`); eski veri taşınmaz, kullanıcıya bırakılır.

Bu kurulumda görülen **tutarsızlık**: `laragon.cmd` hâlâ eski PATH (php-8.3.28, mysql-8.4.11, apache-2.4.66) içeriyor; yani terminal PATH'i ile web PHP'si farklı. Portly'de PATH her değişimde tek yerden üretilmeli.

---

## 7. Diğer özellikler (dil dosyasından çıkarılan tam liste)

- Hepsini Başlat/Durdur, servis bazlı başlat/durdur, Apache reload
- Terminal (Cmder, ortam değişkenleri yüklü), Root klasörü aç, Veritabanı aracı (HeidiSQL)
- Quick app: WordPress (zip indir), Laravel/Symfony (composer komutu), Blank; `AutoCreateDatabase` + `Cached`
- Veritabanı oluştur / proje klon (dosya + DB) / proje sil
- MySQL root şifresi değiştir / sıfırla (`--skip-grant-tables` ile), tüm DB'leri yedekle, otomatik yedek (8 saat, son 5)
- Mail: Mailpit (sendmail_path), eski MailCatcher penceresi, Gmail üzerinden gönderim testi
- Ngrok ile paylaşım, QR kod
- PATH'e ekle/çıkar, Windows açılışında başlat, simge durumunda başlat, sağ tık menüsüne "Terminal/Sublime" ekle
- Profiller (sürüm 7): birden fazla yapılandırma seti
- Eklentiler menüsü (phpMyAdmin, Adminer, phpRedisAdmin, memcached arayüzü)
- SSL aç/kapa, sertifikayı güven deposuna ekle, Sertifika Yöneticisi
- Procfile: özel servisler (ör. `node start`) otomatik başlat
- Dil seçimi (34 dil), bölge

---

## 8. Laragon'un zayıf / eleştirilen yanları (Portly'nin fırsatları)

| Konu | Laragon | Portly hedefi |
|---|---|---|
| Lisans | Kapalı kaynak; ticari kullanım ücretli (EULA 3.1), ücretsiz sürümde satın alma hatırlatmaları | MIT/Apache-2.0 açık kaynak, reklam ve hatırlatma yok |
| Güncelleme | Uygulama güncellemesi elle installer; sadece PHP otomatik | Uygulama + paket kataloğu + şablonlar + diller tam uzaktan güncelleme |
| Dil dosyaları | Numaralı `txt`, sadece programın içinden; katkı süreci yok | JSON, anahtar isimli, GitHub üzerinden katkı, otomatik sistem dili |
| SSL | Tek self-signed cert, her projede yeniden üretim | Yerel kök CA, siteye özel sertifika, tek seferlik güven |
| hosts | node.js bağımlı yükseltme | Kendi küçük yükseltici yardımcısı (UAC), node gerekmez |
| Windows sürümü | Resmi: Win10+ (yeni sürümler), eski Win7 desteği belirsiz | Win7 SP1 → Win11 |
| Yapılandırma | ini + gizli anahtarlar | Tek `portly.json`/ini, hepsi arayüzde ve belgede |
| PHP güvenliği | `session.upload_progress` açık | Varsayılan Off, `allow_url_include=Off` |
| Şeffaflık | Bilinmeyen davranışlar (httpd.conf'u gizlice düzenler) | Yapılan her değişiklik log'da ve "ne yaptım" ekranında |
| Taşınabilirlik | Boşluklu yol sorunlu, sürücü değişince yol gömülü | Göreli yol + ilk açılışta otomatik onarım |
| Nginx+Apache | Aynı anda port çakışması, Nginx 8080'e kaçıyor | Tek web sunucu seçimi, port yönetimi arayüzde |

---

## 9. Win7–Win11 uyumluluğu — kritik kısıtlar

### Uygulamanın kendisi
Win7'de çalışacak bir masaüstü uygulaması için seçenekler:

| Teknoloji | Win7 | Boyut / hız | Not |
|---|---|---|---|
| **Free Pascal + Lazarus (LCL)** | ✔ | Tek exe ~3–5 MB, runtime yok, anında açılır | Laragon'un kendi teknolojisine (Delphi) en yakın; açık kaynak; tepsi/menü/native görünüm hazır |
| **C# .NET Framework 4.8 (WinForms)** | ✔ (Win7 SP1'e 4.8 kurulmalı; Win10/11'de hazır) | exe küçük, açılış ~0,5 s | Geliştirme hızı yüksek, bu makinede `dotnet` SDK ve `csc.exe` mevcut |
| C++ / Qt 5.15 | ✔ | Orta | Qt 6 Win7'yi desteklemez |
| .NET 6/7/8 | ✘ | — | Win7 desteği yok |
| Electron / Tauri / Wails (WebView2) | ✘ (WebView2 Win7 desteği 2023'te bitti; Electron 22 son) | Büyük, yavaş | "Hızlı" hedefiyle çelişir |
| Go ≥1.21 | ✘ | — | Go 1.20 son Win7 sürümü |

**Öneri:** Lazarus (birincil) ya da .NET Framework 4.8 WinForms (ikincil). Karar bölüm 11'de.

### Paketler (asıl sorun burada)
Programın Win7'de çalışması yetmez; sunduğu paketler de çalışmalı:
- **PHP**: 8.x resmi Windows build'leri VS16/VS17 ile derlenir ve Win7'de VC++ redist ile genelde çalışır ama resmi destek yok; 7.4 kesin çalışır. (Doğrulanacak.)
- **MySQL 8.0+**: resmi olarak Win10/Server 2016+. Win7 için **MariaDB 10.x** ya da MySQL 5.7.
- **Apache (ApacheLounge)**: VS16/VS17/VS18 build'leri VC++ 2015-2022 redist ister; Win7'de redist kurulabilir.
- **Node.js**: 14 Win7 son; 16+ çalışmaz.
- **TLS**: Win7'de indirme için TLS 1.2 (KB3140245 / schannel ayarı) gerekir; .NET 4.8 bunu kendiliğinden kullanır, curl.exe kendi OpenSSL'i ile çalışır.

**Sonuç:** Paket kataloğu her paket için `minWindows` alanı taşımalı; Portly işletim sistemini algılayıp Win7'de uygun sürümleri listelemeli (ör. PHP 7.4/8.1, MariaDB 10.6, Node 14).

---

## 10. Portly tasarım önerisi (Laragon'dan alınacaklar + iyileştirmeler)

### Alınacak mimari (kanıtlanmış, iyi çalışıyor)
1. Servis yok, çocuk süreç modeli; tek tepsi uygulaması.
2. `bin\<tür>\<sürüm>` zip tabanlı paketler, `profile` ile aktif sürüm seçimi.
3. Şablondan üretilen yapılandırma + `manifest`/`auto.` sahiplik kuralı.
4. DocumentRoot klasörlerinden otomatik vhost + hosts + sertifika.
5. `usr\` = kullanıcı alanı, güncellemede dokunulmaz.
6. Procfile benzeri özel servis tanımı.

### Portly'nin ekleyecekleri
- **Uzaktan güncelleme sistemi** (hepsi GitHub Releases + statik JSON manifest, sunucu kodu gerekmez):
  - `app.json`: sürüm, indirme URL'si, SHA-256, imza, değişiklik notu → kendini güncelleme (yardımcı exe eski dosyayı değiştirir).
  - `packages.json`: tür / sürüm / URL / SHA-256 / minWindows / mimari → katalog otomatik yenilenir (Laragon'daki `packages.conf` eşdeğeri ama uzaktan).
  - `templates.json`, `lang/*.json`: şablon ve dil güncellemeleri.
  - Kanallar: `stable` / `beta`. Kontrol sıklığı ayarlanabilir, tamamen kapatılabilir (telemetri yok).
- **Çoklu dil**: `lang\tr.json`, `en.json`… ; eksik anahtar İngilizce'ye düşer; sistem dili otomatik; "çeviriye katkı" bağlantısı.
- **Yerel kök CA** ile per-site sertifika; tek seferlik güven.
- **Kendi UAC yükselticisi** (hosts yazımı, sertifika kurulumu) — node bağımlılığı yok.
- **Basit arayüz**: ana pencere = servis listesi (durum ışığı, port, sürüm, Başlat/Durdur), üst şerit = Hepsini Başlat / Web / Veritabanı / Terminal / Root / Ayarlar. Sağ tık tepsi menüsü Laragon ile aynı alışkanlık.
- **PHP güvenlik varsayılanları**: `session.upload_progress.enabled=Off`, `allow_url_include=Off`, `expose_php=Off`.
- **Araç seçimi**: HeidiSQL yerine (lisans/boyut) Adminer + opsiyonel HeidiSQL/DBeaver indirme; Cmder yerine Windows Terminal/PowerShell/cmd + PortableGit.
- Erdem Software markası: "Portly by Erdem Software", `erdemsoftware.com/portly` + GitHub organizasyonu.

### Önerilen klasör yapısı
```
Portly\
├─ Portly.exe, portly-helper.exe (UAC işleri), portly-updater.exe
├─ bin\{apache,php,mysql|mariadb,nginx,nodejs,redis,mailpit,git,composer}\<sürüm>\
├─ etc\{apache,nginx,ssl,apps}\        üretilen yapılandırma
├─ usr\{portly.json, profiles\, tpl\, lang\, Procfile}   kullanıcı alanı
├─ data\<db-sürüm>\   backup\   tmp\   www\   logs\
```

---

## 11. Karar bekleyen noktalar (başlamadan önce)

1. **Geliştirme teknolojisi**: Lazarus/Free Pascal (tek exe, runtime yok, Win7 kesin) mi, C# .NET Framework 4.8 WinForms (bu makinede araçlar hazır, geliştirme daha hızlı; Win7'de .NET 4.8 kurulumu gerekir) mi?
   Önerim: **C# .NET Framework 4.8 WinForms** — geliştirme/bakım hızı, hazır araç zinciri ve Win7 SP1 desteği dengesi en iyi; Win7 kullanıcısı için "önce .NET 4.8 kur" adımı kurulum belgesine girer.
2. **Veritabanı varsayılanı**: MySQL (Win10+) + MariaDB (Win7 dahil) ikisi de katalogda; varsayılan hangisi?
3. **Web sunucu varsayılanı**: Apache (mod_fcgid) — Laragon ile aynı; Nginx seçenek.
4. **Dağıtım**: Portable zip + Inno Setup installer (ikisi de).
5. **Lisans**: MIT önerilir.
6. **Depo**: GitHub `erdemsoftware/portly` (manifest dosyaları da burada barınır, ek sunucu gerekmez).

---

## 12. Verilen kararlar (2026-10-22)

| Konu | Karar |
|---|---|
| Teknoloji | C# .NET Framework 4.8 WinForms, dış bağımlılık yok, tek exe |
| Sürümler | Windows 10/11 sürümü güncelleme alır. Windows 7/8/8.1 için ayrı **Legacy** sürümü: sabit katalog, güncelleme yok |
| Veritabanı | MySQL (en yaygın): modernde 8.4 LTS önerilen, 26.x en yeni; Legacy'de 5.7 |
| Web sunucusu | Apache varsayılan, Nginx seçenek; PHP her ikisinde FastCGI |
| Lisans | MIT, ücretsiz, reklamsız |
| Depo | `github.com/erdemsoftware/portly`; uygulama içinde ve README'de bağış bağlantısı |

Mimari ayrıntılar: [02-mimari.md](02-mimari.md). Yayınlama: [03-yayinlama.md](03-yayinlama.md).
