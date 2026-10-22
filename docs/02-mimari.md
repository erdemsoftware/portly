# Portly mimarisi

Bu belge Portly'nin nasıl çalıştığını, kodda neyin nerede olduğunu ve Laragon'dan nerede ayrıldığını anlatır. Laragon incelemesi için [01-laragon-inceleme.md](01-laragon-inceleme.md) dosyasına bakın.

## Teknoloji

- C#, .NET Framework 4.8, WinForms. Dış paket yok, tek exe (~300 KB).
- .NET Framework 4.8 Windows 10/11'de hazır gelir, Windows 7 SP1'e tek kurulumla eklenir. Bu yüzden iki sürüm de aynı koddan çıkar.
- Sürüm farkı derleme anında belirlenir: `-p:Edition=Legacy` derlemesi `LEGACY` sabitini tanımlar.

| | Modern (win10) | Legacy (win7) |
|---|---|---|
| Uygulama güncellemesi | Var (`manifest/app.json`) | Yok |
| Paket kataloğu | GitHub'dan iner, önbelleğe alınır; PHP sürümleri windows.php.net'ten canlı | Exe içine gömülü, sabit |
| Gömülü yedek katalog | `manifest/packages.json` | `manifest/packages-legacy.json` |

## Kod düzeni (`src/Portly`)

| Klasör | İçerik |
|---|---|
| `Core/` | Yollar, ayarlar (`usr\portly.ini`), INI/JSON yardımcıları, çeviri, günlük, ağ (TLS 1.2 Win7 dahil), süreç yönetimi, job object, Windows yardımcıları |
| `Packages/` | Paket türleri, katalog, kurulum/güncelleme/kaldırma, uygulama güncelleyicisi |
| `Config/` | php.ini yönetimi, siteler, Apache/Nginx yapılandırma üretimi, hosts dosyası, yerel sertifika otoritesi, pano ve araçlar |
| `Services/` | Apache, Nginx, PHP havuzu, MySQL, Redis, Mailpit ve servis yöneticisi |
| `Tools/` | Veritabanı işlemleri, terminal, yeni proje |
| `UI/` | Ana pencere, ayarlar, paketler, yeni proje, hakkında |
| `templates/` | Yapılandırma şablonları ve `http://localhost` panosu |
| `lang/` | Dil dosyaları (JSON) |

## Çalışma modeli

1. **Servis yok, çocuk süreç var.** Apache, MySQL vb. gizli süreç olarak başlar. Hepsi bir Windows *job object* içindedir: Portly çökse bile Windows onları kapatır. Laragon'da bu koruma yoktur.
2. **Paketler zip'ten açılır.** `bin\<tür>\<sürüm-klasörü>`. Aynı türün birden çok sürümü yan yana durur. Etkin sürüm `usr\portly.ini` içindeki `[versions]` bölümündedir.
3. **Yapılandırma şablondan üretilir.** `etc\` her başlatmada yeniden yazılır. Portly, Apache'nin kendi `conf\httpd.conf` dosyasına dokunmaz; Apache `-f etc\apache\httpd.conf` ile başlar.
4. **Sahiplik kuralı.** `etc\apache\sites\auto.*.conf` dosyaları Portly'nindir. Adı `auto.` ile başlamayan dosyalar kullanıcınındır ve korunur. Şablonların `usr\templates\` kopyası hazır gelenden önceliklidir.

## PHP

- Her PHP sürümü kendi FastCGI portunda çalışır: `PhpBasePort` (9100) + kurulu sürümler içindeki sıra.
- Apache `mod_proxy_fcgi`, Nginx `fastcgi_pass` ile bağlanır. Ek Apache modülü (mod_fcgid) gerekmez.
- Bu sayede **her site kendi PHP sürümünü** seçebilir (`usr\sites.ini` → `[site] php=...`).
- `php-cgi.exe -b 127.0.0.1:<port>` ile `PHP_FCGI_CHILDREN` işçi çalışır, `PHP_FCGI_MAX_REQUESTS=0`.
- Çöken PHP 1 dakikada en fazla 3 kez yeniden başlatılır, sonra hata gösterilir.

Windows'a özgü üç ayrıntı, testlerle bulundu:

| Sorun | Çözüm |
|---|---|
| Apache, betik yolunu `/E:/...` olarak gönderir | `ProxyFCGISetEnvIf` ile baştaki `/` silinir |
| `proxy:fcgi://127.0.0.1:9100` sonuna yol eklenince `9100E:/...` olur | İşleyici adresi `/` ile biter |
| Birden çok php-cgi işçisi OPcache belleğini ASLR yüzünden paylaşamaz (PHP 8.5 açılmaz) | `opcache.file_cache` + `opcache.file_cache_fallback=1` |

`php.ini` ilk kurulumda `php.ini-development`'tan oluşturulur. Yollar her başlatmada yenilenir (klasör taşınabilir). Güvenlik ayarları her zaman zorlanır: `session.upload_progress.enabled=Off`, `allow_url_include=Off`, `expose_php=Off`. `mail()` PHP'nin yerleşik SMTP istemcisiyle doğrudan Mailpit'e gider.

## Siteler, hosts, HTTPS

- Proje klasöründeki her geçerli klasör (`a-z 0-9 - _`) bir sitedir: `ad.portly`. `public\index.php` varsa kök `public` olur.
- Geçersiz adlar (boşluk, Türkçe harf) sessizce atlanmaz; Siteler listesinde kırmızı ve açıklamalı görünür.
- hosts dosyasında yalnızca `# >>> Portly >>>` bloğu yönetilir. Yazma izni yoksa Portly kendini `--hosts-apply` ile yönetici olarak çalıştırır; yardımcı yalnızca `127.0.0.1 <geçerli-ad>` satırlarını kabul eder. Alan adı sonu `localhost` seçilirse hosts dosyasına hiç dokunulmaz.
- HTTPS: `usr\ssl\portly-ca.*` yerel kök otoritedir (20 yıl). Kullanıcı bir kez güvenilir yapar. Her siteye 825 günlük, `ad` ve `*.ad` için ayrı sertifika üretilir. Aynı otorite PHP'nin `cacert.pem` paketine eklenir, böylece PHP de `https://*.portly` adreslerini doğrulayabilir.

## Port çakışmaları

Başlatmadan önce `Services/PortGuard.cs` her servisin portlarını denetler:

| Durum | Davranış |
|---|---|
| Portu `bin\` altındaki eski bir Portly servisi tutuyor (ör. çökme sonrası) | Sessizce kapatılır |
| Başka bir program tutuyor (Laragon, XAMPP, IIS, Skype ...) | Pencerede program adı, PID ve yolu gösterilir. Seçenekler: **Boş portları kullan** (8080, 8443, 3307 ... arasından boş olanı bulup ayarlara yazar) ya da **O programları kapat** (onayla) |
| Portu Windows'un kendisi tutuyor (PID 4, http.sys) | Kapatma seçeneği kapalıdır, boş port önerilir |
| PHP'nin iç FastCGI portu dolu | Kullanıcıya sorulmadan aynı aralıkta boş bir port seçilir |

Ayarlar penceresi aynı portun iki servise verilmesini ve PHP port aralığının (ilk port + 100) diğer portlarla çakışmasını engeller. Bir servis yine de port hatasıyla durursa kartında **Çöz** bağlantısı çıkar. Menü > Araçlar > Portları denetle aynı denetimi elle çalıştırır.

## Arayüz

- Sol kenar çubuğu (Servisler, Siteler, Günlük, Paketler, Ayarlar), servis başına marka renkli kartlar, başlat/durdur anahtarı, durum rozetleri, site kartları, renkli konsol günlüğü.
- Açık / koyu tema; varsayılan Windows uygulama temasını izler ve Windows'ta tema değişince anında güncellenir. Windows 10 1809+ üzerinde başlık çubuğu da koyulaşır.
- Tüm simgeler ve kontroller GDI+ ile çizilir (`UI/Glyphs.cs`, `UI/Controls.cs`). Simge yazı tipine ya da resim dosyasına ihtiyaç yoktur; Windows 7'de de aynı görünür.
- Özel düğmeler gerçek Win32 BUTTON olarak kalır; klavye, ekran okuyucu ve erişilebilirlik adları çalışır.

## Ağ

Varsayılan olarak her şey `127.0.0.1`'i dinler: güvenlik duvarı sorusu çıkmaz, ağdan erişilemez. Ayarlar'daki "Ağımdaki diğer cihazlar erişebilsin" seçeneği bunu açar. MySQL 8+ için X protokolü (33060) kapatılır.

## Güncelleme

- **Uygulama (yalnız Modern):** `manifest/app.json` sürüm, zip adresi ve SHA-256 içerir. Zip doğrulanır. Yalnızca program dosyaları değişir; çalışan exe `Portly.exe.old` olarak yeniden adlandırılır ve bir sonraki açılışta silinir. `bin data www usr etc` hiç değişmez.
- **Paketler (yalnız Modern):** katalog 12 saatte bir yenilenir. Aynı serinin yeni yaması (8.4.10 → 8.4.11) "güncelleme" olarak gösterilir; kurulunca etkin sürüm ona geçer, PHP'de eski `php.ini` taşınır.
- **Legacy:** hiçbir uzak istek yapmaz (yalnızca kullanıcı paket indirirken ve CA paketi için).

## Test

`tests/Portly.SmokeTest` motoru arayüz olmadan sınar: gerçek paketleri indirir, servisleri test portlarında (8781, 8744, 3377 ...) başlatır, HTTP/HTTPS, PHP→MySQL, site başına PHP, posta yakalama, Apache↔Nginx geçişi, veritabanı oluşturma/yedekleme ve portların bırakılmasını denetler. Laragon ya da XAMPP çalışırken de çalıştırılabilir.
