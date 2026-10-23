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

## Sunucuya gönder / çek (`Tools/Deploy.cs`, `UI/DeployForm.cs`)

- Özellik `deploy`; yalnız Modern sürümde ve `System32\OpenSSH\ssh.exe` + `System32\tar.exe` varsa görünür (Windows 10 1809+). Profil `usr\deploy.ini` (site başına bir bölüm), anahtar ve `known_hosts` `usr\ssh\` içinde. Oluşturulan anahtar dosyasının izni yalnız kullanıcı + SYSTEM olacak biçimde daraltılır (OpenSSH aksi halde reddeder).
- ssh hep `-o BatchMode=yes -o StrictHostKeyChecking=accept-new` ile çalışır: görünmeyen bir şifre sorusu süreci asla kilitlemez, değişmiş sunucu anahtarı reddedilir.
- Sunucu betikleri C#'ta üretilir ve `sh -c "$(echo <base64> | base64 -d)"` olarak gönderilir; Windows komut satırı ve sunucunun oturum kabuğu betiğin hiçbir karakterini değiştirmez, ssh'nin standart girdisi veri akışına kalır.
- Veri akışları: çekmede `ssh "tar czf - ..." → tar.exe -xzf -` (önce geçici klasöre, sonra hariç tutulanları koruyarak yerel klasörün yerine), `ssh "mysqldump | gzip" → GZipStream → mysql.exe`; göndermede `tar.exe -czf - → ssh "tar xzf -"`, `mysqldump` ya da `wp search-replace --export` → GZipStream → `ssh "gunzip | mysql"`.
- Şifreler: sunucudaki `wp-config.php` / `.env` okunur ya da elle girilen şifre stdin'in ilk satırlarıyla gider (`read -r` bayt bayt okur, kalan akış gunzip'e kalır); sunucuda `umask 077` + `mktemp` ile option dosyasına yazılır, `trap` ile silinir. `--defaults-extra-file` mysqldump'ın ilk seçeneğidir. `sh`'de pipefail olmadığı için mysqldump hatası stderr'e bir işaretle bildirilir.
- Hariç tutma kuralı tar ile aynıdır: desen, yolun ya da üst klasörlerinden birinin sonundaki tam bileşen dizisine uyar (`node_modules`, `*.log`, `storage/logs`).

## Windows hizmeti modu (`Services/ServiceMode.cs`)

- Özellik `serviceMode` + Ayarlar > Genel kutusu. `Portly.exe --service` bir `ServiceBase` çalıştırır: arayüzsüz `ServiceManager`, `Settings.ReadOnly = true` (hizmet `portly.ini`'ye asla yazmaz). Kurulum yükseltilmiş yardımcıyla tek UAC: `--install-service <SID>` → `sc create Portly ... start= auto obj= LocalSystem`, `sc failure` (yeniden başlat), `sc sdset` (kuran kullanıcı UAC'sız başlatıp durdurabilsin), `icacls <kök> /grant *SID:(OI)(CI)M`. `--uninstall-service` durdurup siler.
- Arayüz denetleyici olur: `ServiceMode.IsController` iken `ServiceManager.StartAsync/StopAsync/StartAllAsync/StopAllAsync/StartWebAsync/StopWebAsync/ReloadWebAsync/SwitchWebServerAsync/RestartAsync` işi hizmete yönlendirir. Hepsini başlat/durdur Windows hizmetini başlatır/durdurur (`ServiceController`); tek servis, `reload` (ayar/sürüm değişince) ve `reloadweb` adlandırılmış kanaldan (`portly-service-<kök-hash>`, ACL: SYSTEM, Administrators, kuran kullanıcı) gider. Hizmet durmuşken tek servis açılırsa hizmet `--only <id>` argümanıyla başlar.
- Durumlar: arayüz 2 saniyede bir `status` sorar ve `Service.Mirror()` ile kendi servis nesnelerine yansıtır; kartlar, tepsi simgesi ve "Hepsini başlat" bunu gösterir. Pencereyi kapatmak ya da Windows'tan çıkış hizmeti durdurmaz.
- Kullanıcıya özel şeyler: MySQL root şifresi kullanıcı DPAPI'si ile saklı olduğundan hizmet (LocalSystem) okuyamaz; hizmet modu açıkken bir kopyası `[service] RootPassword` olarak **LocalMachine** DPAPI ile yazılır (bu bilgisayarda `portly.ini`'yi okuyabilen her süreç çözebilir; mod kapatılınca silinir). hosts dosyasını hizmet doğrudan yazar. Yerel CA'ya güven ve kullanıcı PATH'i kullanıcıya özeldir, arayüzde kalır. Hizmetin oluşturduğu dosyalar SYSTEM'e ait olur; `icacls` izni kullanıcının bunları düzenleyebilmesini sağlar.
- Neden süreç içi hizmet (Apache/MySQL'in kendi `-k install` / `--install` hizmetleri yerine): PHP FastCGI havuzları, site başına PHP, port denetimi, hosts ve yapılandırma üretimi zaten `ServiceManager`'da; aynı kod hem pencerede hem hizmette çalışır, ikinci bir yol bakımı gerekmez.
- Sınırlar: MySQL'i farklı seçenekle yeniden başlatan işlemler (root şifresi sıfırlama, kurtarma modu) hizmet modunda yerelde çalışmaya çalışır; önce mod kapatılmalı. Uygulama güncellemesi hizmeti durdurur; yeni sürüm ilk "Hepsini başlat"ta devreye girer.

## Yüksek DPI ve erişilebilirlik

- `app.manifest`: `dpiAwareness = PerMonitorV2, System` (Windows 10 1703+), eski Windows'ta `dpiAware=true` (sistem DPI). `App.config`: `DpiAwareness=PerMonitorV2`; böylece WinForms `WM_DPICHANGED`'i işler.
- `Ui.S()` ve yazı tipleri ana pencerenin bulunduğu monitörün DPI'ını izler (`Ui.SetDpi`). Yazı tipi boyutu = sistem DPI'ındaki punto × (monitör DPI / sistem DPI), her DPI için önbellekte. Ana pencere başka ölçekli monitöre geçince WinForms'un ölçeklemesi iptal edilir ve içerik yeni DPI ile yeniden kurulur (`MainForm.OnDpiChanged` → `ApplyDpi`). İletişim pencerelerini WinForms kendisi ölçekler.
- UI Automation: Windows'un BUTTON sınıfı vekili sahibi çizilen (owner-draw) düğmeleri adsız "Pane" olarak gösteriyordu, ekran okuyucular basamıyordu. `UI/Accessibility.cs` (`UiaProvider`) kontrollerin `WM_GETOBJECT`'ine kendi UIA sağlayıcısıyla cevap verir: ModernButton = Button + Invoke, ToggleSwitch = CheckBox + Toggle, Card = adlı Group, Pill = Text, BrandTile = Image. MSAA nesneleri de (AccessibleRole/Name/DefaultAction) aynı bilgiyi verir.
- Klavye: odak çerçevesi birincil/kırmızı düğmelerde beyaz iç çerçeve olarak çizilir (vurgu renginde kaybolmasın); `Ui.Apply` her iletişim penceresine Esc ile kapatmayı ekler.
