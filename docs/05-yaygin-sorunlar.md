# Windows yerel geliştirme ortamlarında yaygın sorunlar

Bu belge Laragon, XAMPP, WampServer, MAMP (Windows), AMPPS, UwAmp, Laravel Herd (Windows) ve Local (Flywheel) kullanıcılarının en sık yaşadığı sorunları toplar. Kaynaklar: Stack Overflow, GitHub sorunları (özellikle `leokhoa/laragon`), resmi belgeler ve forumlar. Her sorun için Portly'nin bugünkü durumu, kaynak koda bakılarak (Ekim 2026, `main` dalı) işaretlendi. Amaç, Portly'nin bu sorunları baştan önlemesi ya da kendisi düzeltmesi.

İşaretler: **Var** = Portly bunu zaten çözüyor · **Kısmen** = bir kısmı çözülüyor ya da çözümde risk var · **Yok** = hiç ele alınmıyor. *(doğrulanmadı)* = kaynaklarla kesinleşmeyen bilgi.

---

## 0. Bu rapordan sonra yapılanlar (sürüm 1.0.0)

| Madde | Sonuç |
|---|---|
| 3.1 Dışlanmış port aralıkları | Yapıldı: `netsh ... excludedportrange` okunur, bu portlar dolu sayılır ve boş port önerisinde atlanır; PHP havuzu gerekirse başka porta geçer. |
| 3.2 Çökme sonrası açılmama | Yapıldı: hata günlüğünden InnoDB/Aria hasarı tanınır, **Kurtarma modunda başlat** (`innodb_force_recovery=1`) menüsü. |
| 3.3 Windows kapanırken | Yapıldı: `SetProcessShutdownParameters` + `ShutdownBlockReasonCreate`, servisler sırayla temiz durdurulur. |
| 3.4 `localhost` → `::1` | Yapıldı: Apache/Nginx/MySQL `::1` üzerinde de dinler. |
| 3.5 hosts engeli | Yapıldı: yazdıktan sonra geri okunur, DNS önbelleği temizlenir, engellenirse uyarı ve `localhost` önerisi. |
| 3.6 Defender yavaşlığı | Yapıldı: isteğe bağlı, uyarılı istisna (yönetici). |
| 3.7 İmzasız exe | Açık: SignPath Foundation ile ücretsiz açık kaynak imzası önerilir. |
| 3.9 Yerel CA | Yapıldı: CA anahtarı DPAPI ile korunur; terminalde `CURL_CA_BUNDLE`, `GIT_SSL_CAINFO`, `NODE_EXTRA_CA_CERTS`, `REQUESTS_CA_BUNDLE`; WordPress'e mu-plugin. |
| 3.10 Paylaşım | Yapıldı: tünel adresi WordPress'te yönlendirmesiz çalışır, paylaşım yalnız seçilen siteye. |
| 3.11 Klasör sorunları | Yapıldı: Sistem denetimi (OneDrive, ASCII dışı yol, uzun yol desteği tek tıkla). |
| 3.12 MySQL uyumluluk | Yapıldı: esnek SQL modu seçeneği; içe aktarmada MariaDB/0900 harmanlama dönüşümü. |
| 3.13 root şifresi | Yapıldı: **root şifresini sıfırla** (`--init-file`). |
| 3.14 Çakışan PHP | Yapıldı: PATH'te başka PHP ve `PHPRC` uyarısı. |
| 3.15 FastCGI havuzu | Yapıldı: `PHP_FCGI_MAX_REQUESTS` ile işçi yenileme, dakikada en çok 3 çökme yeniden başlatma. |
| Yerelleştirilmiş WordPress | Yapıldı: `wp-config.php` sabit adıyla doldurulur (çevrilmiş yer tutucular ve tuzlar dahil). |

## 1. Sorun tablosu (sıklık ve etkiye göre)

| # | Sorun | Nerede görülür | Neden olur | Portly'de durum | Önerilen önlem |
|---|---|---|---|---|---|
| 1 | Apache/Nginx başlamıyor, 80/443 portu dolu | Hepsi (XAMPP, Wamp, Laragon, Local, Herd, MAMP) | IIS/W3SVC, http.sys (PID 4), Skype, VMware `vmware-hostd`, TeamViewer, Docker, başka bir yığın | Var | Bugünkü port denetimi yeterli. Ek olarak W3SVC için "Elle başlat yap" seçeneği sunulabilir. |
| 2 | MySQL başlamıyor, 3306 dolu | XAMPP, Laragon, MAMP, Local | Kurulu `MySQL80` hizmeti, başka bir yığının mysqld'si | Var | Sahibi bir Windows hizmetiyse "hizmeti durdur ve elle başlata çevir" seçeneği eklenebilir. |
| 3 | "MySQL shutdown unexpectedly", çökme sonrası açılmıyor (aria_log, ibdata1, redo log) | XAMPP (çok sık), Laragon, MAMP | Elektrik kesilmesi, zorla kapatma, işlem öldürme, MariaDB Aria sistem tabloları | Kısmen | Hata günlüğünü okuyan bir kurtarma sihirbazı (bkz. §3.2). Ayrıca iş nesnesi (job object) MySQL'i sert öldürüyor, bu bir risk. |
| 4 | Port boş görünüyor ama bağlanmıyor: `(OS 10013) An attempt was made to access a socket...` | Hyper-V, WSL2 ya da Docker kullanan herkes | Windows'un ayırdığı port aralıkları (WinNAT `excludedportrange`); dinleyen bir işlem yok | **Yok** | Başlatmadan önce dışlanmış aralıkları da denetle, önerilen portları bu aralıkların dışından seç (§3.1). |
| 5 | Eksik ya da eski Visual C++ çalışma kitaplığı (MSVCR110, VCRUNTIME140 "not compatible", "Entry Point Not Found", 0xc000007b) | Wamp (çok sık), XAMPP, Laragon (PHP 8.1/8.2), Local | PHP/Apache derlemesi daha yeni bir VC++ istiyor; 32/64 bit uyumsuzluğu | Var | Sürüm (minor) denetimi var, korunmalı. |
| 6 | composer/curl: `SSL certificate problem: unable to get local issuer certificate` (cURL 60) | XAMPP, Wamp, Laragon | Windows PHP'sinde CA paketi yok, `curl.cainfo` boş | Var | `curl.cainfo` ve `openssl.cafile` zaten ayarlı. Paket her güncellemede yenilenmeli. |
| 7 | WordPress güncelleme ve indirmelerde cURL 60, yerel `https://site` adresine loopback başarısız | Laragon (GitHub'da 19 yorumlu konu), Local | WordPress `WP_Http`, PHP ayarını değil kendi `wp-includes/certificates/ca-bundle.crt` dosyasını kullanır; yerel CA bu dosyada yok | Kısmen | Portly'nin yerel CA'sını WordPress'e tanıtan bir mu-plugin (§3.9). |
| 8 | Sanal host (`site.test`) açılmıyor: `DNS_PROBE_FINISHED_NXDOMAIN` | Laragon, Herd (HerdHelper), Local, Wamp | hosts dosyası yazılamadı (antivirüs, salt okunur, yönetici izni yok), DNS önbelleği | Kısmen | Yazdıktan sonra geri okuyup doğrula, `ipconfig /flushdns` çalıştır, antivirüs engelini tanı (§3.5). |
| 9 | Defender'ın "SettingsModifier:Win32/HostsFileHijack" uyarısı, hosts girdilerinin silinmesi | Tüm hosts kullanan araçlar | Defender, hosts değişikliğini tehdit sayabilir | Kısmen | Blok bozulursa uyar ve onar, kullanıcıya açıklama göster (§3.5). |
| 10 | `localhost` ile MySQL bağlantısı ~1 sn gecikiyor, Node `fetch('http://localhost')` ECONNREFUSED veriyor | XAMPP, Wamp, Laragon, Herd | `localhost` önce `::1` adresine çözülür; sunucu yalnız 127.0.0.1'de dinlediği için önce zaman aşımı olur | Kısmen | MySQL ve web sunucusunu `::1` adresinde de dinlet (§3.4). |
| 11 | Yerel HTTPS'e güvenilmiyor (Firefox, `ERR_SSL_KEY_USAGE_INCOMPATIBLE`, ad uyuşmazlığı) | Laragon 7/8 (en çok yorum alan konular), XAMPP, Wamp | Kendinden imzalı sertifika, eksik keyUsage, Firefox'un ayrı güven deposu | Var | Kök CA + SAN + keyUsage doğru. Firefox Windows deposunu kullanıcının eklediği kökler için okur; eski Firefox için `security.enterprise_roots.enabled` ipucu verilebilir. |
| 12 | PHP dışındaki araçlar yerel CA'ya güvenmiyor (Node/Vite, Git, Python) | Herd, Laragon | Node kendi CA listesini kullanır; Git'in OpenSSL arka ucu ve Python'un certifi'si ayrı | Kısmen | Terminalde `NODE_EXTRA_CA_CERTS`, `NODE_USE_SYSTEM_CA=1`, `SSL_CERT_FILE` ayarla (§3.9). |
| 13 | Windows Defender `npm install`, `composer install` ve PHP'yi çok yavaşlatıyor | Hepsi | Gerçek zamanlı tarama binlerce küçük dosyayı tarar | **Yok** | İsteğe bağlı, açık onaylı dışlama ya da Dev Drive önerisi (§3.6). |
| 14 | Antivirüs yanlış pozitifi: exe ya da DLL karantinaya alınıyor | Laragon 7.0.3 (`Trojan:Win32/Phonzy.A!ml`), Apache derlemeleri, XAMPP (Sophos) | İmzasız ikili dosyalar, ML tabanlı sezgiler | Kısmen | Portly.exe'yi imzala, SHA-256 gösterilsin, Microsoft'a yanlış pozitif gönderme rehberi (§3.7). |
| 15 | SmartScreen ya da Smart App Control imzasız exe'yi engelliyor | İmzasız tüm araçlar | Windows 11 Smart App Control imzasız ya da itibarı olmayan exe/DLL'i engeller | **Yok** | Kod imzalama (SignPath Foundation, OSS için ücretsiz) (§3.7). |
| 16 | Çakışan PHP: IDE ya da başka bir terminal yanlış `php.exe` ve `php.ini` kullanıyor | XAMPP + Laragon birlikte kurulunca, Wamp | Sistem PATH'inde `C:\xampp\php`, genel `PHPRC` ya da `PHP_INI_SCAN_DIR` | Kısmen | Başlangıçta `where php` ve genel `PHPRC` denetimi, uyarı (§3.14). |
| 17 | Proje OneDrive klasöründe: PHP dosyaları bulamıyor, eşitleme kilitleniyor | Hepsi | Files On-Demand yer tutucu dosyalar (reparse point), `node_modules` eşitlemesi | **Yok** | Proje ya da Portly kökü OneDrive altındaysa uyar (§3.11). |
| 18 | Türkçe ya da ASCII dışı karakterler (kullanıcı adı, klasör): MySQL, Composer, Local bozuluyor | Local (resmî forum), Wamp, XAMPP | ANSI kod sayfası ile UTF-8 uyuşmazlığı; `%APPDATA%` ve `%TEMP%` kullanıcı profilinde | Kısmen | Kök yolda ASCII denetimi; çocuk işlemlere `TMP`, `TEMP` ve `COMPOSER_HOME` olarak Portly içinde ASCII bir yol ver (§3.11). |
| 19 | Uzun yol: `Filename too long` (git), `node_modules` silinemiyor | Hepsi | MAX_PATH 260 sınırı | **Yok** | `LongPathsEnabled` denetimi, tek UAC ile açma önerisi, terminalde `git config core.longpaths true` (§3.11). |
| 20 | Program Files'a kurulum: yazma izni yok, UAC sorunları | XAMPP (kurulumda uyarır), Wamp | UAC sanallaştırması, izin eksikliği | Var | Taşınabilir `C:\Portly` yapısı. Kök yazılabilir mi diye denetle, Program Files altındaysa uyar. |
| 21 | Laravel ya da WordPress kurulumunda `requires ext-zip / ext-intl / ext-fileinfo` | XAMPP, Wamp | Eklentiler `php.ini` dosyasında kapalı | Var | Yaygın eklentiler yeni `php.ini` dosyasında açık. İçe aktarılan `php.ini` için de denetle. |
| 22 | Büyük SQL içe aktarma: upload sınırı, zaman aşımı, `MySQL server has gone away` | XAMPP, Wamp, Laragon | `upload_max_filesize`, `max_execution_time`, `max_allowed_packet` | Var | 512M ve 300 sn ayarlı. Çok büyük dökümler için komut satırından içe aktarma düğmesi eklenebilir. |
| 23 | Eski uygulama MySQL 8'de bozuluyor (`ONLY_FULL_GROUP_BY`, strict mode, `0000-00-00` tarihleri) | XAMPP (MariaDB'den MySQL'e geçenler), Laragon | MySQL 5.7+/8 varsayılan `sql_mode` | **Yok** | Veritabanı başına "eski uygulama uyumluluk modu" anahtarı (§3.12). |
| 24 | `The server requested authentication method unknown to the client` | MySQL 8.4/9 + PHP ≤ 7.3 | MySQL 8.4'te `mysql_native_password` kapalı geliyor, 9.0'da kaldırıldı | **Yok** | Sürüm uyumluluk uyarısı; 8.4'te `mysql_native_password=ON` anahtarı (§3.12). |
| 25 | `Unknown collation: 'utf8mb4_0900_ai_ci'` | MySQL 8 dökümünü MariaDB veya 5.7'ye aktaran XAMPP kullanıcıları | Bu harmanlama yalnız MySQL 8'de var | **Yok** | İçe aktarmada harmanlamayı otomatik çevir (§3.12). |
| 26 | `lower_case_table_names` farkı: Linux'ta çalışan döküm Windows'ta bozuluyor, ya da tersi | Hepsi | Windows'ta varsayılan 1, Linux'ta 0; MySQL 8'de ilk kurulumdan sonra değiştirilemez | Kısmen | Veri klasörü oluşturulurken açıkça yaz ve bilgilendir *(davranış MySQL sürümüne göre doğrulanmalı)*. |
| 27 | Root şifresi değişti ya da unutuldu, phpMyAdmin `#1045` hatası veriyor | XAMPP (`resetroot.bat`), Laragon (GitHub özellik isteği) | phpMyAdmin yapılandırmasındaki eski şifre | Kısmen | Şifre DPAPI ile saklanıyor; "root şifresini sıfırla" aracı yok (§3.13). |
| 28 | Aynı anda iki site açıkken siteler gecikiyor ya da yanıt vermiyor | Laragon (21 yorumlu konu), Local | Az sayıda php-cgi alt işlemi, uzun istekler, takılan bir işlem | Kısmen | Havuz başına 6 alt işlem var. Sağlık denetimi ve takılan işlemi yeniden başlatma eklenmeli (§3.15). |
| 29 | php-cgi belleği şişiyor, işlem hiç yenilenmiyor | Laragon ("PHP CGI process not recycling") | `PHP_FCGI_MAX_REQUESTS=0` sınırsız demek; sızıntılar birikir | Kısmen | Portly de `0` kullanıyor. Kontrollü yenileme gerekli (§3.15). |
| 30 | Windows kapanırken veritabanı temiz kapanmıyor | Tüm "hizmet olmayan" yığınlar | Windows uygulamaya birkaç saniye tanır; tepsideki gizli pencere engelleme yapamaz | Kısmen | `ShutdownBlockReasonCreate` ve `SetProcessShutdownParameters` (§3.3). |
| 31 | Yerel alan adı adres çubuğunda Google aramasına gidiyor | Özel TLD kullanan herkes (`.portly`, `.loc`) | Chrome bilinmeyen TLD'yi arama sorgusu sayar | Kısmen | `.portly` riskleri ve `.test` / `.localhost` seçeneği (§3.8). |
| 32 | `.dev` ya da `.app` alan adı zorla HTTPS'e gidiyor | Eski Valet, Laragon, Local kurulumları | HSTS preload listesi | Var | `.portly` kullanılıyor. `.dev`, `.app`, `.page`, `.foo` için ad doğrulamasında uyarı verilebilir. |
| 33 | Laravel/Vite: HTTPS sayfada `http://localhost:5173` engelleniyor (mixed content), HMR çalışmıyor | Herd, Laragon | Vite geliştirme sunucusu HTTP, site HTTPS | Kısmen | Site sertifikasının yolunu Vite'a ver (env ya da `detectTls` benzeri) (§3.9). |
| 34 | Windows'ta Laravel ya da WordPress yavaş (sayfa başına saniyeler) | Laragon, XAMPP | Defender, Xdebug her istekte açık, OPcache kapalı | Var/Kısmen | OPcache açık, Xdebug "trigger" kipinde, Apache `AcceptFilter none`. Defender için §3.6. |
| 35 | Apache Windows'ta takılıyor, `AcceptEx` hataları | XAMPP, Wamp | Windows'a özel gönderim (sendfile/AcceptEx) sorunları, güvenlik duvarı yazılımları | Var | `EnableSendfile Off`, `EnableMMAP Off`, `AcceptFilter none` zaten ayarlı. |
| 36 | `.htaccess` çalışmıyor (WordPress kalıcı bağlantıları 404 veriyor) | Wamp, el ile yapılan vhost'lar | `AllowOverride None`, `mod_rewrite` kapalı | Var (Apache) / Kısmen (Nginx) | Nginx'e geçince `.htaccess` okunmaz. WordPress ve Laravel için hazır Nginx kuralları ve uyarı. |
| 37 | Laravel kökü yanlış (`/public` görünmüyor) | XAMPP, Wamp | Belge kökü proje klasörü | Var | `public\index.php` algılanıyor. |
| 38 | `mail()` çalışmıyor | XAMPP, Wamp | Windows'ta sendmail yok | Var | Mailpit, `php.ini` içinde SMTP olarak ayarlı. |
| 39 | Xdebug her şeyi yavaşlatıyor ya da IDE bağlanmıyor | Hepsi | `start_with_request=yes`, yanlış port | Var | `trigger` kipi. Port 9003 doluysa uyarı eklenebilir. |
| 40 | `node`/`npm` tanınmıyor, açık terminal eski PATH'i kullanıyor | Laragon (cmder), Herd | PATH değişikliği açık oturumlara yansımaz | Var/Kısmen | Portly terminali doğru PATH ile açıyor. Kullanıcı PATH'i değişince `WM_SETTINGCHANGE` yayınla, "terminali yeniden açın" uyarısı göster. |
| 41 | Paylaşılan (tünel) WordPress ya da Laravel sitesi yerel adrese yönlendiriyor, CSS/JS yüklenmiyor | Local Live Link, Laragon ngrok | `Host` başlığı yerel ada çevriliyor; site mutlak URL'ler ve kanonik yönlendirme üretiyor | Kısmen | Paylaşım sırasında WordPress için URL filtresi, Laravel için `X-Forwarded-*` notu (§3.10). |
| 42 | LAN erişimi açık ama telefondan bağlanılamıyor | Wamp ("Put Online"), XAMPP | Güvenlik duvarı kuralı yok, ağ profili "Genel", `.portly` adı telefonda çözülmüyor | Kısmen | LAN açılınca tek UAC ile yalnız Özel profil için güvenlik duvarı kuralı ekle; LAN IP adresini göster. |
| 43 | LAN açıkken şifresiz root ve phpMyAdmin dışarıya açılıyor | XAMPP (eski güvenlik sayfası), Wamp | Varsayılan şifresiz root, araçlara her yerden erişim | Kısmen | phpMyAdmin/Adminer `Require local` ile korunuyor. LAN kipinde MySQL'i yine 127.0.0.1'de tut ya da şifre iste. |
| 44 | Yerel kök CA'nın anahtarı çalınırsa tüm HTTPS trafiği taklit edilebilir | mkcert, Laragon, Portly | Makine genelinde güvenilen ve hiçbir alan kısıtı olmayan CA; anahtar düz metin | Kısmen (risk) | Ad kısıtlaması (Name Constraints), anahtarı DPAPI ile koruma, daha kısa ömür (§3.10). |
| 45 | Satır sonları (CRLF) yüzünden `.sh` betikleri Docker veya Linux'ta çalışmıyor | Windows'ta Git kullanan herkes | `core.autocrlf=true` | Yok (kapsam dışı) | Proje oluştururken `.gitattributes` şablonu önerilebilir. Düşük öncelik. |
| 46 | Lisans penceresi, kapalı kaynak, güvenlik güncellemelerinin gecikmesi | Laragon 7+ (GitHub'da birçok konu) | Ücretli lisans modeli | Var | Açık kaynak ve imzalı otomatik güncelleme. Güven için kod imzalama önemli (§3.7). |
| 47 | Hızlı uygulama kurulumu (WordPress) boş veritabanı oluşturuyor, indirme takılıyor | Laragon | İndirme hatası, `wp-config` ayarlanmıyor | Kısmen *(kontrol edilmeli)* | İndirmede zaman aşımı ve yeniden deneme; `wp-config.php` dosyasının oluştuğunu doğrula. |
| 48 | Zip'ten açılan dosyalar "engellenmiş" (Mark of the Web) | Taşınabilir tüm araçlar | Explorer, zip'in `Zone.Identifier` işaretini açılan her dosyaya kopyalar | Kısmen | Portly'nin kendi indirdiği paketlerde bu işaret olmaz. İlk açılışta kendi klasöründe `Zone.Identifier` akışlarını sil *(etkisi doğrulanmadı)*. |

---

## 2. Öncelik özeti

En çok karşılaşılan sorunlar şunlar: port çakışması (1, 2), MySQL çökmesi (3), VC++ (5), SSL/CA (6, 7, 11) ve hosts dosyası (8). Portly bunların çoğunu zaten çözüyor. Asıl boşluklar şurada:

- Windows'a özgü "görünmez" engeller: dışlanmış port aralıkları, Defender, Smart App Control, OneDrive, uzun yol.
- Veritabanı uyumluluğu ve kurtarma.
- Yerel CA'nın PHP dışındaki araçlara tanıtılması.

---

## 3. Öne çıkan "Kısmen" ve "Yok" maddeleri için teknik öneriler

### 3.1 Dışlanmış port aralıkları (Hyper-V / WinNAT / WSL2 / Docker), durum: Yok

**Sorun:** `ProcessUtil.IsPortInUse` yalnız `GetActiveTcpListeners()` sonucuna bakıyor. Port Windows'un ayırdığı bir aralıktaysa dinleyen bir işlem yoktur. Portly portu boş sanır, Apache ya da MySQL ise `(OS 10013) ... forbidden by its access permissions` hatasıyla açılmaz. Ayrıca Portly'nin önerdiği yedek portlar (8080, 8443, 3307, 20000–21000) da bu aralıklara düşebilir. Bu aralıklar yeniden başlatma ve güncelleme sonrasında değişebilir.

**Öneri:**
1. Aralıkları oku. Yönetici izni gerekmez:
   `netsh interface ipv4 show excludedportrange protocol=tcp` (IPv6 için `ipv6`). Çıktı dilden bağımsız iki sayı sütunudur. "*" ile işaretli satırlar yönetici ayırmasıdır.
   Daha sağlam ikinci yol: portu gerçekten bağlamayı dene. `new Socket(...).Bind(new IPEndPoint(IPAddress.Loopback, port))` hata kodu `10013` (WSAEACCES) dönerse port "ayrılmış" demektir.
2. `PortGuard.Check` bu durumu ayrı bir çakışma türü olarak göstermeli. Örnek metin: "Port 3306 Windows tarafından ayrılmış (Hyper-V/WSL/Docker). Sahibi bir program değil."
3. `FreePort` önerileri hem dinleyicileri hem dışlanmış aralıkları atlamalı.
4. İleri seçenek (tek UAC): portu kalıcı ayırmak için `net stop winnat` → `netsh int ipv4 add excludedportrange protocol=tcp startport=80 numberofports=1` → `net start winnat`. Bu sistem geneli bir değişiklik olduğu için yalnız kullanıcı onayıyla yapılmalı. Not: "add excludedportrange" ile ayrılan port, programların bağlanmasına izin verir. Bu kural yalnız Windows'un o portu dinamik aralığa almasını engeller *(belgelerde ayrıntı az; doğrulanmalı)*.

Kaynaklar: Microsoft KB "Error 10013 (WSAEACCES) is returned when a second bind to an excluded port fails"; dev.to "An attempt was made to access a socket..."; dev.to "Why port 8000 suddenly stopped working".

### 3.2 MySQL/MariaDB çökme sonrası açılmıyor, durum: Kısmen

**Gözlem:** XAMPP'te en sık görülen hata budur. Çoğu durumda neden, MariaDB'nin Aria sistem tabloları ve `aria_log.*` dosyalarıdır. Rehberlerin önerdiği "ibdata1'i sil" adımı veri kaybına yol açar. Portly'de temiz kapatma (mysqladmin) ve yedek var. Ancak:
- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, Portly çöktüğünde mysqld'yi sert öldürür. InnoDB bundan genelde kendini kurtarır, ama MariaDB gelince Aria tabloları "crashed" olarak işaretlenebilir.
- Açılış hatasını kullanıcıya anlaşılır biçimde açıklayan bir akış yok.

**Öneri:**
1. **Kirli kapanışı tespit et.** Başlarken veri klasöründe `.pid` dosyası kaldıysa ya da `Settings`'te bir "çalışıyordu" bayrağı açık kaldıysa, önceki kapanış temiz değildir.
2. **Açılış başarısızsa `mysql-error.log` dosyasının son 200 satırını ara ve her desene karşılık bir eylem öner:**
   - `Aria recovery failed` / `aria_log` → mysqld durdurulmuşken `aria_chk -r <datadir>\mysql\*.MAI`, ardından `aria_log.*` ve `aria_log_control` dosyalarını `backup\` altına taşı.
   - `is marked as crashed` → başlat ve `mariadb-check --auto-repair --all-databases` (MySQL'de `mysqlcheck`) çalıştır.
   - `InnoDB: ... corruption` / `Assertion failure` → `innodb_force_recovery=1` ile salt okunur başlat, `mysqldump --all-databases` al, ardından yeni veri klasörüne geri yükle. Sayıyı 1'den başlat, en fazla 4'e çık. 4 ve üstü veri kaybına yol açabilir.
   - `Can't start server: Bind on TCP/IP port` → port akışına (§3.1) yönlendir.
3. **Hiçbir zaman otomatik silme yapma.** Her adımdan önce veri klasörünün zip anlık görüntüsünü `backup\` altına al.
4. **İş nesnesi davranışı:** Portly normal kapanırken önce mysqladmin ile kapatıyor, bu doğru. Çökme anı için `AppDomain.UnhandledException` içinde de kısa bir `mysqladmin shutdown` denemesi yapılabilir.

Kaynaklar: tonjoo, dev.to "MySQL shutdown unexpectedly", homehost.com.br (ibdata1 uyarısı), MariaDB Jira MDEV-32806, MariaDB hata 1194.

### 3.3 Windows kapanırken temiz durdurma, durum: Kısmen

**Gözlem:** `OnFormClosing` içinde `CloseReason.WindowsShutDown` ele alınıyor ve 15 sn bekleniyor. Ancak Windows uygulamaya yalnız birkaç saniye tanır (kaynaklar 2 ile 5 sn arasında değişiyor). Ayrıca Portly tepsiye gizlendiğinde görünür bir üst düzey penceresi olmaz. Bu durumda `ShutdownBlockReasonCreate` çağrılmazsa uygulama hemen sonlandırılır.

**Öneri:**
- `WndProc` içinde `WM_QUERYENDSESSION` geldiğinde `ShutdownBlockReasonCreate(Handle, "Veritabanı güvenle kapatılıyor…")` çağır, durdurma işini arka planda başlat, bitince `ShutdownBlockReasonDestroy` çağır.
- Program başında `SetProcessShutdownParameters(0x3FF, 0)` çağır. Böylece Portly, kapanış sırasında kendi çocuk işlemlerinden önce bilgilendirilir.
- MySQL durdurmayı kapanış yolunda öne al. Önce MySQL, sonra web ve PHP durdurulsun.

Kaynaklar: Raymond Chen (Old New Thing), meziantou.net "Prevent Windows shutdown".

### 3.4 `localhost` → `::1` gecikmesi, durum: Kısmen

**Gözlem:** `mysql.ini.tpl` içinde `bind-address=127.0.0.1` var. Apache ve Nginx de 127.0.0.1'de dinliyor. Kullanıcı `.env` dosyasına `DB_HOST=localhost` yazarsa ya da Node'dan `http://localhost` çağırırsa, Windows önce `::1` adresini dener. Kapalı porta yapılan bağlantı Windows'ta yeniden denendiği için yaklaşık 1 sn gecikme ya da ECONNREFUSED görülür. Node 17 ve sonrası `localhost` için önce IPv6 adresini kullanır.

**Öneri:**
- MySQL 8.0.13+ ve MariaDB 10.11+ için `bind-address=127.0.0.1,::1` yaz. MariaDB'de virgüllü liste 10.11.1 sürüm notlarında (MDEV-24377) var. MySQL için sürüm belgeden doğrulanmalı. Daha eski sürümlerde 127.0.0.1'de kal ve `.env` ile `wp-config` için `127.0.0.1` öner.
- Apache: `Listen 127.0.0.1:80` satırına ek olarak `Listen [::1]:80`. Nginx: `listen [::1]:80;`. LAN kipinde zaten `0.0.0.0` ya da `[::]` kullanılıyor.
- Proje oluşturucu ve içe aktarıcı, `DB_HOST` değerini her zaman `127.0.0.1` yazsın.

Kaynaklar: bugs.php.net #51085, Drupal #1064342, dev.to "The localhost trap", ASP.NET JavaScriptServices #1588.

### 3.5 hosts dosyası: antivirüs engeli, doğrulama, DNS önbelleği, durum: Kısmen

**Gözlem:** Portly bloğu yazıyor ve salt okunur özniteliği geçici kaldırıyor, bu iyi. Ancak birkaç eksik var:
- Yazdıktan sonra dosya geri okunup doğrulanmıyor. Defender ya da başka bir koruma yazmayı sessizce geri alabilir. Local ve Herd'de en sık görülen DNS hatası budur.
- `ipconfig /flushdns` çalıştırılmıyor.
- Hata kodu 2 genel bir mesajla gösteriliyor.

**Öneri:**
1. `ApplyElevated` yazdıktan sonra dosyayı yeniden okusun ve bloğu karşılaştırsın. Uyuşmazlık ya da `UnauthorizedAccessException`/`IOException` varsa ayrı bir hata kodu dönsün (ör. 4 = "antivirüs ya da korumalı klasör engelledi").
2. Başarılı yazmadan sonra, aynı yükseltilmiş işlemde `DnsFlushResolverCache` (dnsapi.dll, belgelenmemiş ama yaygın) çağır ya da `ipconfig /flushdns` çalıştır.
3. Açılışta bir sağlık denetimi yap: `Dns.GetHostAddresses("<ilk site>.portly")` sonucu 127.0.0.1 değilse ve blok dosyada yoksa, "hosts girdileri silinmiş (büyük olasılıkla Defender HostsFileHijack)" uyarısı göster ve tek tıkla onarma sun.
4. Yedek: blok yazılamıyorsa siteler `https://site.localhost` adresiyle de açılabilsin. Chrome ve Firefox `*.localhost` adlarını hosts dosyası olmadan 127.0.0.1'e çözer (bkz. §3.8).
5. Chrome'un Güvenli DNS (DoH) özelliğinin hosts dosyasını atladığına dair forum iddiaları var. Ancak Chromium belgesi yerleşik çözümleyicinin hosts dosyasını kendisinin okuduğunu söylüyor. Bu nedenle bu konuda bir önlem gerekmiyor *(doğrulanmadı)*.

Kaynaklar: Local forumu "Disable host file locking error", Herd "Resolving common issues", Microsoft .NET blog (Defender localhost girdisini silmişti), gridinsoft (HostsFileHijack), Laragon DeepWiki.

### 3.6 Defender yavaşlığı, durum: Yok

**Gözlem:** Defender, Windows'ta Node ve Composer araçlarının yavaşlığının bilinen ana nedeni. Filament'in resmî belgesi de proje klasörünü dışlamayı öneriyor. Microsoft'un önerdiği modern çözüm ise Dev Drive (ReFS) ve onun "performance mode" özelliği. Bu modda tarama kapanmaz, dosya açma işlemi bittikten sonra eşzamansız yapılır.

**Öneri:** Hiçbir işlemi kullanıcıya sormadan yapma.
- Ayarlar > Performans'a "Defender dışlaması ekle" düğmesi koy. Tek UAC ile yalnız yol dışlaması ekle, işlem (`php.exe`, `node.exe`) dışlaması ekleme: `Add-MpPreference -ExclusionPath 'C:\Portly\www','C:\Portly\tmp','C:\Portly\data'`. npm önbelleği (`%LOCALAPPDATA%\npm-cache`) ve Composer önbelleği de eklenebilir.
- Ekranda açıkça "Bu klasörlerdeki dosyalar artık taranmayacak" uyarısı göster. Kaldırma düğmesi `Remove-MpPreference` çalıştırsın.
- Kurumsal makinede Tamper Protection ya da GPO dışlamayı engelleyebilir. Bu durumda sonucu `(Get-MpPreference).ExclusionPath` ile doğrula (yönetici gerekir).
- Windows 11'de Dev Drive varsa ya da kullanıcı oluşturmak isterse, proje klasörünü Dev Drive'a taşımayı öner.

Kaynaklar: Filament "Optimizing local development", Microsoft Learn (performance mode, Add-MpPreference), JetBrains "Antivirus impact on build speed".

### 3.7 İmzasız exe: SmartScreen, Smart App Control, yanlış pozitif, durum: Yok/Kısmen

**Gözlem:** Laragon 7.0.3 exe'si Defender'da `Trojan:Win32/Phonzy.A!ml` olarak işaretlendi. Bu konu GitHub'da 13 yorum aldı. Windows 11'in Smart App Control özelliği imzasız exe ve DLL'leri doğrudan engelleyebilir. Portly'nin derleme betiklerinde imzalama adımı yok.

**Öneri:**
- Portly.exe'yi, kurulum programını ve güncelleyiciyi imzala. Açık kaynak projeler için SignPath Foundation ücretsiz sertifika veriyor. Koşullar: OSI lisansı, kaynaktan doğrulanabilir derleme (GitHub Actions), her sürüm için el ile onay, sitede yayımlanmış bir imzalama politikası. İmzalı olsa bile SmartScreen itibarının oluşması zaman alır.
- Paket kataloğundaki üçüncü taraf ikili dosyalar (Apache Lounge, windows.php.net) zaten üretici imzalı ya da imzasız gelir. Bunlar için SHA-256 doğrulaması sürmeli. Karantina olursa kullanıcıya hangi dosyanın etkilendiği ve yanlış pozitif gönderim adresi gösterilsin: https://www.microsoft.com/wdsi/filesubmission
- Smart App Control açıkken bir alt işlem başlamazsa (`ERROR_ACCESS_DENIED` ya da Code Integrity olayı), hata mesajında bu olasılık belirtilsin. SAC durumu `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` altındaki `VerifiedAndReputablePolicyState` değerinden okunabilir (0 = kapalı, 1 = açık, 2 = değerlendirme) *(doğrulanmadı)*.

Kaynaklar: GitHub leokhoa/laragon sorun listesi, Microsoft Q&A "Application signed and blocked by Smart App Control", signpath.org, about.signpath.io/product/open-source.

### 3.8 `.portly` TLD seçimi, durum: Kısmen (risk)

**Riskler:**
1. Chrome'un TLD listesi gömülü ve değiştirilemez. Kullanıcı adres çubuğuna `magaza.portly` yazarsa sayfa açılmaz, Google araması yapılır. Sondaki `/` ya da `https://` öneki gerekir. Portly bağlantıları her zaman şemayla açıyorsa sorun yalnız elle yazımda çıkar.
2. ICANN'in 2026 yeni gTLD turu açıldı (başvuru penceresi 30 Nisan – 12 Ağustos 2026, kaynaklara göre). `.portly` adının gerçek bir TLD olma olasılığı çok düşük ama sıfır değil. RFC 6761 ve RFC 2606 ile yalnız `.test`, `.localhost`, `.example` ve `.invalid` kalıcı olarak ayrılmış durumda.
3. Node, curl ve Python gibi araçlar `*.localhost` için hosts dosyasına bakar. Tarayıcılar ise `*.localhost` adını kendileri loopback'e çözer.

**Öneri:** Ayarlara alan adı soneki seçeneği ekle: `portly` (varsayılan), `test`, `localhost`. `.localhost` hosts dosyası yazılamadığında bile tarayıcıda çalışır, iyi bir yedektir. Ad doğrulamasında `dev`, `app`, `page`, `foo` gibi HSTS preload sonekleri engellensin.

Kaynaklar: OpenNIC wiki (Chrome alternatif TLD), textslashplain.com (HSTS .dev), Mozilla Bugzilla 1220810 (`let-localhost-be-localhost`), ICANN yeni gTLD programı sayfaları.

### 3.9 Yerel CA'nın PHP dışı araçlara ve WordPress'e tanıtılması, durum: Kısmen

**Gözlem:** Portly, `php.ini` içinde `curl.cainfo` ve `openssl.cafile` ayarlarını yapıyor. Ancak:
- **WordPress:** `WP_Http`, kendi `wp-includes/certificates/ca-bundle.crt` dosyasını kullanır. Bu yüzden `https://site.portly` adresine yapılan loopback, Site Health, WP-Cron ve REST denetimleri cURL 60 ile başarısız olur. Laragon'da bunun için 19 yorumlu bir GitHub konusu var.
- **Node/Vite:** Kendi CA listesini kullanır. Node 22.15+, 23.9+ ve 24+ sürümlerinde `NODE_USE_SYSTEM_CA=1` ile Windows deposu okunur. Daha eski sürümler için `NODE_EXTRA_CA_CERTS` gerekir.
- **Git:** OpenSSL arka ucu kullanılıyorsa yerel CA'yı tanımaz. **Python:** `requests` certifi kullanır.

**Öneri:**
1. WordPress algılanan sitelere (wp-config.php varsa), kullanıcı onayıyla `wp-content/mu-plugins/portly-local-ca.php` dosyasını yaz. İçinde `http_request_args` filtresi olsun; URL'nin sunucusu `.portly` ile bitiyorsa `$args['sslcertificates'] = '<Portly CA paketi>'` atasın. Bu yöntem Kevin Leary'nin `.test` için önerdiği yöntemin aynısı. `sslverify=false` kullanma.
2. Portly terminalinin ortam değişkenleri: `NODE_EXTRA_CA_CERTS=<bundle>`, `NODE_USE_SYSTEM_CA=1`, `SSL_CERT_FILE=<bundle>`, `REQUESTS_CA_BUNDLE=<bundle>`, `CURL_CA_BUNDLE=<bundle>`, `GIT_SSL_CAINFO=<bundle>`. Alternatif: Git'te `http.sslBackend=schannel`.
3. Vite: sitenin sertifika ve anahtar yolu `usr\ssl\sites\<host>.crt` gibi sabit bir yerde olmalı ve "Site > Vite için HTTPS bilgisi" menüsünde gösterilmeli. Laravel'in `detectTls` seçeneği Herd ve Valet yollarına bakıyor, Portly yolunu bilmez.

Kaynaklar: kevinleary.net "Fixing WordPress cURL errors", nodejs.org "Enterprise network configuration", Laravel Vite belgesi, GitHub leokhoa/laragon "cURL error 60".

### 3.10 Paylaşım (Cloudflare tüneli) ve yerel CA güvenliği, durum: Kısmen

**Paylaşım:** `Share.cs`, `--http-host-header site.portly` kullanıyor. Bu yüzden WordPress `home` ve `siteurl` değerlerine göre ziyaretçiyi `https://site.portly` adresine yönlendirir ve bağlantıları o adresle üretir. Ziyaretçi bu adı çözemez. Öneri: paylaşım açıkken WordPress sitelerine geçici bir mu-plugin koy. Bu eklenti `HTTP_X_FORWARDED_HOST` (cloudflared `trycloudflare.com` adresini başlıkta taşır *(doğrulanmadı)*) değerine göre `option_home` ve `option_siteurl` filtrelerini geçersiz kılsın, `$_SERVER['HTTPS']='on'` ayarlasın. Paylaşım kapanınca eklenti silinsin. Laravel için `TrustProxies` notu gösterilsin.

**Yerel CA güvenliği:**
- CA 20 yıl geçerli, ad kısıtlaması yok. Özel anahtarı `usr\ssl` altında düz metin PEM olarak duruyor. mkcert belgesi bu anahtar için "makinenizdeki güvenli istekleri yakalama yetkisinin tamamını verir" uyarısını yapıyor.
- Öneri 1: CA oluşturulurken `X509 NameConstraints` (kritik) uzantısı ekle. İzin verilenler: `.portly`, `.test`, `.localhost`, `localhost`, IP 127.0.0.0/8 ve ::1/128. Böylece anahtar çalınsa bile `google.com` için sertifika üretilemez. Chrome ve Windows CryptoAPI ad kısıtlamalarını uygular *(Firefox'ta yerel kök CA için davranış doğrulanmalı)*.
- Öneri 2: Anahtarı `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` ile şifreli sakla.
- Öneri 3: CA ömrünü 10 yıla ya da daha kısaya indir.

Kaynaklar: mkcert README, Cloudflare/WordPress yönlendirme döngüsü yazıları.

### 3.11 Klasör sorunları: OneDrive, ASCII dışı karakter, uzun yol, durum: Yok/Kısmen

**OneDrive:** Files On-Demand ile dosyalar yer tutucu (reparse point) olur ve PHP ikincil dosyaları bulamayabilir (bugs.php.net #78220, #75384). `node_modules` eşitlemesi de çok yavaştır.
- Tespit: `%OneDrive%`, `%OneDriveConsumer%`, `%OneDriveCommercial%` ortam değişkenleri ve `HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder`. Proje klasörü ya da Portly kökü bunların altındaysa, ya da `FileAttributes.ReparsePoint` / `RecallOnDataAccess (0x400000)` özniteliği varsa uyar. Bilinen Klasör Taşıma (Known Folder Move) yüzünden "Belgeler" ve "Masaüstü" de OneDrive altında olabilir.

**ASCII dışı karakterler:** Local'in resmî forumunda "Windows kullanıcı adında özel karakter varsa site kurulamıyor" konusu var. Portly kökü `C:\Portly` olduğu için güvenli. Ancak `%TEMP%`, `%APPDATA%\Composer` ve npm önbelleği kullanıcı profilinde, örneğin `C:\Users\Müşerref\...`.
- Öneri: `Paths.HasSpaceInRoot` denetimine ASCII dışı karakter denetimini de ekle. Proje klasörü için de uyar. Bütün çocuk işlemlere (php-cgi, mysqld, terminal) `TMP` ve `TEMP` olarak `C:\Portly\tmp`, `COMPOSER_HOME` olarak `C:\Portly\usr\composer` ver. Bu yalnız kullanıcı profili ASCII dışıysa yapılsın ya da her zaman yapılsın.

**Uzun yol:** `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` (DWORD) 0 ise, proje oluştururken ya da npm kurarken tek UAC ile 1 yapmayı öner. Not: değişiklik yalnız `longPathAware` manifestine sahip uygulamaları etkiler. PHP 7.1 ve sonrası uzun yolları zaten destekler, Explorer desteklemez. Portly terminalinde `git config --global core.longpaths true` önerilsin.

Kaynaklar: bugs.php.net #78220 ve #75384, Microsoft Learn "Maximum Path Length Limitation", Local forumu (özel karakterler).

### 3.12 MySQL sürüm ve uyumluluk tuzakları, durum: Yok

1. **`sql_mode`:** MySQL 5.7/8 varsayılanı `ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION`. Eski PHP uygulamaları bununla bozulur. Öneri: Ayarlar > Veritabanı > "Eski uygulama uyumluluğu" anahtarı. Açıkken `my.ini` içine `sql_mode="NO_ENGINE_SUBSTITUTION"` yazılsın, kapalıyken satır silinsin.
2. **Kimlik doğrulama:** MySQL 8.4'te `mysql_native_password` varsayılan olarak kapalı, 9.0'da kaldırıldı. PHP 7.2 ve 7.3 `caching_sha2_password` ile bağlanamaz. Öneri: bir site PHP ≤ 7.3 kullanıyorsa ve MySQL ≥ 8.4 ise uyar. 8.4'te `[mysqld] mysql_native_password=ON` anahtarı ve kullanıcı için `ALTER USER ... IDENTIFIED WITH mysql_native_password` sunulsun. 9.x'te tek çözüm 8.4'e ya da MariaDB'ye geçmek.
3. **Harmanlama (collation):** MySQL 8 dökümündeki `utf8mb4_0900_ai_ci`, MariaDB ve 5.7'de "Unknown collation" hatası verir. Öneri: Portly'nin içe aktarma aracı hedef MariaDB ya da 5.7 ise akışta `utf8mb4_0900_ai_ci` → `utf8mb4_unicode_520_ci` (ya da `utf8mb4_unicode_ci`) çevirisi yapsın. Dosya diske yeniden yazılmasın, akışta değiştirilsin.
4. **`lower_case_table_names`:** Windows'ta varsayılan 1. MySQL 8'de ilk kurulumdan sonra değiştirilemez. Veri klasörü oluşturulurken bu değer `my.ini` dosyasına açıkça yazılsın ve belgelensin. İsteğe bağlı olarak 2 değeri (adlar diskte büyük/küçük harf korunarak saklanır) sunulabilir *(MySQL 8'de Windows için 2 değerinin desteği doğrulanmalı)*.

Kaynaklar: MySQL 8.0 SQL mode SSS, Oracle MySQL blog "PHP and MySQL 9", MySQL 8.4 "Native pluggable authentication", cPanel "Unknown collation utf8mb4_0900_ai_ci".

### 3.13 Root şifresini sıfırlama ve phpMyAdmin kilitlenmesi, durum: Kısmen

**Gözlem:** XAMPP bunun için `resetroot.bat` sunuyor. Laragon'da "MySQL 8 için root şifresi değiştirme" bir özellik isteği olarak duruyor.

**Öneri:** Menüye "root şifresini sıfırla" ekle. Akış:
1. mysqld'yi durdur.
2. `--init-file=<tmp>\reset.sql` ile başlat. Dosya içeriği: `ALTER USER 'root'@'localhost' IDENTIFIED BY '...'; FLUSH PRIVILEGES;`. MariaDB'de de çalışır. `--skip-grant-tables` yerine bu yöntem daha güvenli, çünkü ağ açılmaz.
3. Normal başlat. Yeni şifreyi DPAPI ile sakla, phpMyAdmin/Adminer otomatik giriş yapılandırmasını güncelle, geçici SQL dosyasını sil.

### 3.14 Sistemde çakışan PHP veya MySQL kurulumları, durum: Kısmen

**Gözlem:** Kullanıcı daha önce XAMPP ya da Wamp kurduysa, sistem PATH'inde `C:\xampp\php` bulunur. Portly'nin terminali doğru çalışır. Ancak VS Code, PhpStorm ve Windows Terminal başka bir `php.exe` görür. Genel bir `PHPRC` ya da `PHP_INI_SCAN_DIR` değişkeni de başka bir `php.ini` yükletebilir.

**Öneri:**
- Açılışta `HKCU\Environment` ve `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment` altındaki `Path`, `PHPRC` ve `PHP_INI_SCAN_DIR` değerlerini oku. `php.exe`, `mysql.exe` ya da `composer` başka bir köke işaret ediyorsa "Sisteminizde başka bir PHP var: C:\xampp\php" bilgisini göster.
- "PATH'i Portly'ye göre ayarla" seçeneği yalnız kullanıcı PATH'ini değiştirsin. Ardından `SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "Environment")` yayınla ve "açık terminalleri yeniden başlatın" uyarısı göster.
- Çalışan bir `MySQL80` ya da `wampmysqld64` hizmeti bulunursa, port çakışması olmasa bile bilgi ver.

### 3.15 PHP FastCGI havuzu: eşzamanlılık, takılma, bellek, durum: Kısmen

**Gözlem:** `PHP_FCGI_MAX_REQUESTS=0` kullanılıyor. Bu, php-cgi alt işlemlerinin hiç yenilenmeyeceği anlamına gelir; sızıntılar birikir. Laragon'daki "PHP CGI process not recycling" konusu bunu anlatıyor. Havuz başına 6 alt işlem var. Uzun bir istek (ör. 300 sn'lik içe aktarma), bir Xdebug kesme noktası ya da WordPress'in kendine yaptığı loopback istekleri bu işlemleri tüketebilir. Laragon'da "iki site birlikte açıkken yanıt yok" konusu bu yüzden çıkıyor.

**Öneri:**
1. Sızıntıya karşı Portly'nin gözetimi: alt işlemlerin çalışma kümesi belleği ölçülsün. Belirli bir eşiği (ör. 1 GB) aşan ya da istek almayan bir işlem, havuz yeniden başlatılarak yenilensin. Diğer seçenek `PHP_FCGI_MAX_REQUESTS=1000`. Bu durumda web sunucusu tarafında, istek sırasında kapanan bir işlemin 502 vermemesi için yeniden deneme olmalı *(Windows php-cgi ile mod_proxy_fcgi davranışı test edilmeli)*.
2. Sağlık denetimi: her havuzun FastCGI portuna 5 saniyede bir bağlantı denemesi yapılsın. Port yanıt vermiyorsa ya da tüm alt işlemler yüksek CPU'da takılıysa, havuz yeniden başlatılsın ve günlüğe yazılsın.
3. Alt işlem sayısı ayarlanabilir kalsın. Varsayılan `max(6, işlemci sayısı)` olabilir. Xdebug açıkken en az 2 alt işlem ayrı tutulsun, ya da "hata ayıklama açık, diğer istekler bekleyebilir" bilgisi gösterilsin.

---

## 4. Kaynaklar

- https://tonjoo.com/error-mysql-shutdown-unexpectedly/
- https://dev.to/tanvir8321/how-i-solved-the-mysql-shutdown-unexpectedly-error-a-step-by-step-guide-2nke
- https://www.homehost.com.br/blog/tutoriais/mysql/mysql-shutdown-unexpectedly/
- https://medium.com/@arslantariq_dev/how-to-fix-the-xampp-error-mysql-shutdown-unexpectedly-7c1764e0ec3
- https://jira.mariadb.org/browse/MDEV-32806
- https://mariadb.com/docs/server/reference/error-codes/mariadb-error-codes-1100-to-1199/e1194
- https://ithelp.ithome.com.tw/articles/10253789
- https://learn.microsoft.com/en-us/answers/questions/229871/pid4-(system32-ntoskrnl-exe)-blocking-the-port-80
- https://blog.51sec.org/2019/12/pid-4-listening-on-port-80-or-port-12345.html
- https://windowsreport.com/xampp-port-80-443-in-use-skype-fix/
- https://themeisle.com/blog/xampp-error-apache-shutdown-unexpectedly/
- https://ovindupathiraja.medium.com/solving-xampp-mysql-port-conflict-error-d1522322e358
- https://support.microsoft.com/en-us/kb/3039044
- https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/error-10013-wsaeacces-is-returned
- https://dev.to/trademark18/an-attempt-was-made-to-access-a-socket-4nh9
- https://dev.to/tahsin000/why-port-8000-suddenly-stopped-working-on-my-local-machine-and-how-i-fixed-it-4a12
- https://www.digitalocean.com/community/tutorials/apache-network-error-ah00072-make_sock-could-not-bind-to-address
- https://www.thewindowsclub.com/msvcr110-dll-is-missing
- https://blog.devsense.com/2022/fix-php-warning-vcruntime140.dll
- https://www.php.net/manual/en/curl.configuration.php
- https://discourse.cakephp.org/t/composer-diagnose-yielding-an-error-60-message-no-matter-what-i-do/12442
- https://kevinleary.net/blog/fixing-wordpress-curl-errors
- https://community.auth0.com/t/unable-to-complete-network-request-curl-error-60-ssl-certificate-problem-certificate-has-expired-see-https-curl-haxx-se-libcurl-c-libcurl-errors-html/132291/2
- https://bugs.php.net/bug.php?id=51085
- https://www.drupal.org/node/1064342
- https://dev.to/skucherenko/the-localhost-trap-a-10-second-database-connection-on-windows-3le7
- https://github.com/aspnet/JavaScriptServices/issues/1588
- https://mariadb.com/docs/release-notes/community-server/10.11/10.11.1.md
- https://laragon.org/docs/pretty-urls
- https://deepwiki.com/leokhoa/laragon/4.2-virtual-hosts
- https://community.localwp.com/t/disable-host-file-locking-error/22444
- https://localwp.com/community/t/could-not-update-hosts-files/24862/3
- https://herd.laravel.com/docs/windows/troubleshooting/common-issues
- https://laracasts.com/discuss/channels/general-discussion/herd-site-cant-be-reached-issue
- https://devblogs.microsoft.com/dotnet/asp-net-web-development-server-stops-working/
- https://blog.gridinsoft.com/settingsmodifier-win32-possiblehostsfilehijack/
- https://chromium.googlesource.com/chromium/src/+/main/net/dns/README.md
- https://support.mozilla.org/en-US/kb/setting-certificate-authorities-firefox
- https://wiki.mozilla.org/CA/AddRootToFirefox
- https://joshua.hu/mozilla-firefox-trusting-system-root-stores-qwacs-eu
- https://github.com/duesee/mkcert
- https://textslashplain.com/2017/12/05/strict-transport-security-for-dev/
- https://ma.ttias.be/chrome-force-dev-domains-https-via-preloaded-hsts/
- https://wiki.opennic.org/chrome_alternative_tlds
- https://bugzilla.mozilla.org/show_bug.cgi?id=1220810
- https://code.djangoproject.com/ticket/31010
- https://newgtldprogram.icann.org/en/node/13
- https://www.eurodns.com/blog/the-next-round-of-gtld-dotbrand-applications-what-you-need-to-know
- https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode
- https://blogs.windows.com/windowsdeveloper/2023/06/01/dev-drive-performance-security-and-control-for-developers/
- https://learn.microsoft.com/pt-br/powershell/module/defender/add-mppreference
- https://filamentphp.com/docs/4.x/introduction/optimizing-local-development
- https://intellij-support.jetbrains.com/hc/en-us/articles/360006298560
- https://blog.johnnyreilly.com/windows-defender-step-away-from-npm
- https://ilikekillnerds.com/2017/02/03/yarn-slow-windows-disable-windows-defender/
- https://github.com/leokhoa/laragon/issues?q=is%3Aissue+sort%3Acomments-desc
- https://github.com/leokhoa/laragon/discussions/985
- https://laragon.org/pricing
- https://cert.pl/en/posts/2024/02/CVE-2024-0864
- https://www.apachelounge.com/viewtopic.php?p=33988
- https://community.sophos.com/on-premise-endpoint/f/sophos-endpoint-software/1720/xampp-false-positive/3282
- https://learn.microsoft.com/en-us/answers/questions/5791210/application-signed-and-blocked-by-smart-app-contro
- https://abp.io/support/questions/10365
- https://signpath.org/
- https://about.signpath.io/product/open-source
- https://nolongerset.com/mark-of-the-web-details/
- https://bugs.php.net/78220
- https://bugs.php.net/75384
- https://support.microsoft.com/en-gb/topic/file-and-excel-related-errors-when-directory-is-synced-with-onedrive-964bafe6-d365-446a-80b0-f964bde4bc71
- https://community.localwp.com/t/unable-to-provision-site-due-to-special-characters-in-windows-username/14419
- https://bugs.php.net/bug.php?id=50203
- https://learn.microsoft.com/en-ie/Windows/Win32/fileio/maximum-file-path-limitation
- https://discourse.hkvstore.com/t/root-composer-json-requires-php-extension-ext-intl-but-it-is-missing-from-your-system/5140
- https://codemia.io/knowledge-hub/path/import_file_size_limit_in_phpmyadmin
- https://docs.oracle.com/cd/E17952_01/mysql-8.0-en/faqs-sql-modes.html
- https://docs.oracle.com/cd/E17952_01/mysql-8.4-en/sql-mode.html
- https://blogs.oracle.com/mysql/post/php-and-mysql-9
- https://lefred.be/content/mysql-9-0-its-time-to-abandon-the-weak-authentication-method/
- https://docs.oracle.com/cd/E17952_01/mysql-8.4-en/native-pluggable-authentication.html
- https://support.cpanel.net/hc/en-us/articles/4406175692695-Unknown-collation-utf8mb4-0900-ai-ci-error-when-importing-database
- https://azdigi.com/blog/website-tutorials/fix-the-unknown-collation-utf8mb4_0900_ai_ci-error
- https://docs.cloud.google.com/sql/docs/mysql/known-issues-in-place-major-version-upgrade-to-8.0
- https://learntech.medsci.ox.ac.uk/wordpress-blog/?p=151
- https://archive.virtualmin.com/node/19921
- https://redmine.pfsense.org/projects/pfsense/repository/2/revisions/8134cc27e8a39d7d6b3201254960f49bc58948eb
- https://www.apachelounge.com/viewtopic.php?p=20868
- https://devblogs.microsoft.com/oldnewthing/20120614-00/?p=7373
- https://www.meziantou.net/prevent-windows-shutdown-or-session-ending-in-dotnet.htm
- https://www.dedoimedo.com/computers/wordpress-site-health-curl-error-28.html
- https://wordpress.org/support/?p=14133309
- https://nodejs.org/learn/http/enterprise-network-configuration
- https://laravel.com/docs/vite
- https://laracasts.com/discuss/replies/918877
- https://discourse.hkvstore.com/t/cant-find-node-js-installed-as-part-of-laragon/9022
- https://community.localwp.com/t/windows-issue-locals-router-is-having-trouble-starting/14223
- https://community.localwp.com/t/starting-or-stopping-a-second-site-causes-an-err-connection-refused-on-first-site/25990
- https://support.mamp.info/knowledgebase.php?article=26
- https://documentation.mamp.info/en/MAMP-PRO-Windows/Troubleshooting/General/General2/
- https://jessitron.com/2019/11/11/line-endings-in-git/
- https://know.cynderhost.com/about/cloudflare-wordpress-and-redirect-loops/
