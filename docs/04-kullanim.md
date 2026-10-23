# Portly kullanım rehberi

## Kurulum yeri

Portly'yi `C:\Portly` klasörüne koyun (kurulum programı da buraya kurar). Projeleriniz varsayılan olarak `C:\Portly\www` içindedir. Başka bir klasör isterseniz Ayarlar > Proje klasörü'nden değiştirin.

Zip dosyasını açmadan, WinRAR içinden çift tıklayarak çalıştırırsanız Portly geçici bir klasörden çalıştığını fark eder ve kendini `C:\Portly` içine kopyalamayı önerir.

## Site adresleri

| Klasör | Adres |
|---|---|
| `www\magaza` | `https://magaza.portly` |
| `www\magaza\public\index.php` varsa | site kökü `public` olur (Laravel, Symfony) |
| `www\musteriler\acme` (grup) | `https://acme.musteriler.portly` |

**Gruplar:** Bir klasör kendisi proje değilse ama içinde projeler varsa grup sayılır. Proje işaretleri şunlardır: `index.php`, `index.html`, `composer.json`, `package.json`, `artisan`, `wp-config.php`, `public\index.php`. Grubun içindeki her proje `alt.grup.portly` adresini alır. Grubun kendisi de `grup.portly` adresinde klasör listesi olarak açılır.

Tahmini değiştirmek için `usr\sites.ini` dosyasına yazın:

```ini
[musteriler]
group=1      ; her zaman grup say
[eski-site]
group=0      ; hiçbir zaman grup sayma
```

Klasör adları yalnızca `a-z 0-9 - _` içermelidir. Boşluk ya da Türkçe harf içeren klasörler Siteler sayfasında kırmızı görünür ve nedeni yazılır.

**Alan adı sonu:** Varsayılan `portly`. Ayarlar'dan değiştirilebilir. `localhost` seçilirse hosts dosyasına hiç dokunulmaz. Tarayıcıya elle yazarken ilk seferde sona `/` ekleyin (`magaza.portly/`), çünkü Chrome tanımadığı uzantıları arama sanabilir. Portly'den tıklayarak açılan adreslerde bu gerekmez.

## HTTPS

Portly ilk başlatmada yerel sertifika otoritesini ("Portly Local CA") güvenilir yapmak için bir kez yönetici izni ister. Bundan sonra her yeni site uyarısız açılır. Site kartlarındaki **HTTPS** rozeti durumu gösterir. Turuncu "güvenilmiyor" rozetine tıklamak güven adımını tekrarlar.

## Elle sürüm ekleme (katalogda olmayan sürümler)

Örneğin eski bir PHP 7.3 ya da özel derlenmiş bir Apache eklemek için:

**Yol 1: Paketler penceresinden**
1. Paketler > **Elle ekle**.
2. Paket türünü seçin (üstteki filtre bir türdeyse bu adım atlanır).
3. Resmi siteden indirdiğiniz `.zip` dosyasını seçin. Zip yerine açılmış bir klasör eklemek için zip penceresinde İptal deyin, ardından klasörü seçin. Composer ve Adminer için tek dosya (`composer.phar`, `adminer.php`) da seçilebilir.
4. Portly dosyaları `bin\<tür>\<ad>` içine koyar. PHP için `php.ini`'yi hazırlar; sürüm, servis kartındaki listede görünür.

**Yol 2: Klasörü elle kopyalamak**

Açılmış paketi doğrudan `bin\<tür>\<sürüm-adı>` içine kopyalayın. Portly bir sonraki açılışta görür. Klasörün içinde şu dosya bulunmalıdır:

| Tür | Klasör | Olması gereken dosya |
|---|---|---|
| Apache | `bin\apache\httpd-2.4.xx` | `bin\httpd.exe` |
| Nginx | `bin\nginx\nginx-1.xx` | `nginx.exe` |
| PHP | `bin\php\php-8.x.x-nts-...` | `php-cgi.exe` (NTS sürümü) |
| MySQL | `bin\mysql\mysql-8.x.x-winx64` | `bin\mysqld.exe` |
| Node.js | `bin\nodejs\node-v22...` | `node.exe` |
| Redis | `bin\redis\redis-x.x` | `redis-server.exe` |
| Mailpit | `bin\mailpit\mailpit-x.x` | `mailpit.exe` |
| Composer | `bin\composer\composer-x` | `composer.phar` |
| Adminer / phpMyAdmin | `bin\adminer\...` | `index.php` |

Klasör adında sürüm numarası olsun (ör. `php-7.3.33-nts`). Portly sürümü addan okur. MySQL'in her ana serisi (5.7, 8.4 ...) kendi veri klasörünü kullanır, yani sürüm eklemek mevcut veritabanlarını bozmaz.

## Karşılama ekranı

Portly her açılışta üç adımlı bir karşılama ekranı gösterir: paketleri kur, başka programdan aktar, başlat. "Bu ekranı bir daha gösterme" işaretlenince bir daha çıkmaz. Ayarlar dosyasında `ShowWelcome=1` yapılarak geri getirilebilir.

## Başka programdan geçiş

Kenar çubuğu > **İçe aktar**. Desteklenenler:

| Program | Projeler | Veritabanları |
|---|---|---|
| Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP | ✓ | ✓ |
| Laravel Herd, Local (Flywheel) | ✓ | — (kendi uygulamalarında tutarlar) |

Kurulumlar tüm sürücülerin kökünde ve Program Files altında aranır.

- **Projeler:** Portly eski proje klasörünü olduğu gibi kullanabilir (kopyalama yok) ya da projeleri `C:\Portly\www` içine kopyalar.
- **Veritabanları:** "Veritabanlarını listele" ile seçin. Eski MySQL çalışmıyorsa geçici olarak boş bir portta başlatılır; dökümü eski kurulumun kendi `mysqldump` aracıyla alınır ve Portly'nin MySQL'ine yüklenir. XAMPP'ın MariaDB'sine özgü satırlar yüklemede düzeltilir.
- Eski kurulumda hiçbir şey değiştirilmez ya da silinmez.

## Siteyi internette paylaşmak

Site kartındaki **⋯** menüsü > **İnternette paylaş**. Cloudflare'in hesapsız, ücretsiz geçici tüneli açılır ve `https://…trycloudflare.com` linki panoya kopyalanır. Paylaşım durdurulana ya da Portly kapanana kadar sürer. Kendi adresini veritabanında saklayan uygulamalar (WordPress gibi) yerel adrese yönlendirebilir.

## Posta yakalayıcı

PHP'nin gönderdiği tüm postalar dışarı çıkmadan yakalanır. Ayarlar > Servisler'den üç araçtan biri seçilir: **Mailpit** (varsayılan), **MailHog** (çok küçük, Windows 7'de de çalışır) ya da **smtp4dev** (gelişmiş arayüz). Hepsi aynı portları kullanır, PHP tarafında değişiklik gerekmez.

## Xdebug

Menü > PHP > **Xdebug**. Etkin PHP sürümüne uygun DLL xdebug.org'dan indirilir. Ayarlar: `debug, develop` kipi, istek tetikleyiciyle başlar (`XDEBUG_TRIGGER` ya da tarayıcı eklentisi), istemci `127.0.0.1:9003` (VS Code ve PhpStorm varsayılanı). Aynı menüden kapatılır.

## Otomatik veritabanı yedeği

Ayarlar > MySQL > **Otomatik yedek** (varsayılan kapalı): 6, 12 ya da 24 saatte bir. MySQL çalışırken tüm veritabanları `backup\mysql\auto-*.sql` olarak kaydedilir. Yalnızca son N otomatik yedek tutulur; elle alınan yedekler silinmez.

## Yeni proje şablonları

PHP: Boş, WordPress (Portly dilinde), Laravel, Symfony, CodeIgniter 4, CakePHP, Drupal. JavaScript: Vue, React, Svelte (Vite), Nuxt, Next.js, Angular, Astro (Node.js gerekir). Composer ve npm şablonları bir terminal penceresinde kurulur.

**Tek tıkla WordPress:** Hızlı erişimdeki **WordPress** kutucuğu ya da Yeni proje > WordPress > "Tam kurulum". Veritabanı oluşturulur, `wp-config.php` doldurulur (yerelleştirilmiş paketlerde de), WP-CLI ile kurulum biter; yönetici kullanıcısı `admin` ve rastgele şifre ekranda gösterilir. Site kökü (`public`, `web`, `webroot`) kendiliğinden bulunur.

## Port çakışması

Bkz. [02-mimari.md](02-mimari.md#port-çakışmaları). Kısacası "Hepsini başlat" öncesinde her port denetlenir. Dolu portlar için **Boş portları kullan** ya da **O programları kapat** seçilir.

## Veritabanları ve araçlar

- **MySQL ya da MariaDB:** Ayarlar > Servisler > Veritabanı motoru. Her motor ve ana sürüm kendi veri klasörünü kullanır (`data\mysql-8.4`, `data\mariadb-11.8`), geçişte veri karışmaz.
- **PostgreSQL** (5432, kullanıcı `postgres`, şifresiz), **MongoDB** (27017), **Memcached** (11211): Paketler'den indirip Servisler'den açın. PHP'de `pdo_pgsql` / `pgsql` hazır.
- **SQLite:** `sqlite3` terminalde; görsel araç olarak DB Browser for SQLite.
- **Görsel araçlar:** Hızlı erişim > Veritabanı menüsü. phpMyAdmin ve Adminer tarayıcıda; HeidiSQL, DBeaver, Beekeeper Studio, DbGate masaüstünde (Portly'nin MySQL bağlantısıyla açılır). CloudBeaver yalnızca Docker ile dağıtıldığı için katalogda yok; aynı motoru kullanan DBeaver var.

### Veritabanı dili (karşılaştırma) ve varsayılan tablo türü

Ayarlar > MySQL > **Karşılaştırma (dil)** ve **Varsayılan tablo türü**. Varsayılan `utf8mb4_unicode_ci` ve InnoDB. Türkçe sıralama ve büyük/küçük harf (ı/İ) için `utf8mb4_turkish_ci` seçin. Liste etkin motora ve sürüme göre değişir: `utf8mb4_0900_ai_ci` / `utf8mb4_tr_0900_ai_ci` yalnızca MySQL 8+, `utf8mb4_uca1400_ai_ci` / `utf8mb4_uca1400_turkish_ai_ci` yalnızca MariaDB 10.10+; Aria tablo türü yalnızca MariaDB. Seçim `my.ini`'ye (`character-set-server`, `collation-server`, `default-storage-engine`) yazılır ve veritabanı yeniden başlar. Sunucunun bilmediği bir değer kalırsa (ör. MariaDB'ye geçince MySQL'e özgü dil) `utf8mb4_unicode_ci` kullanılır.

### Yeni veritabanı

Menü > Veritabanı > **Veritabanı oluştur**: ad, karşılaştırma (varsayılan seçili) ve isteğe bağlı yalnızca o veritabanına yetkili yeni kullanıcı + şifre (rastgele önerilir). Bağlantı bilgileri ve `.env` satırları kopyalanabilir olarak gösterilir. Yeni proje sihirbazının oluşturduğu veritabanları da varsayılan karşılaştırmayı kullanır.

### Büyük SQL dosyası içe aktarma

Menü > Veritabanı > **SQL dosyası içe aktar...**: `.sql`, `.sql.gz` ya da `.zip` (içindeki ilk `.sql`). Hedef: var olan veritabanı, yeni veritabanı (varsayılan karşılaştırmayla) ya da dosyada yazan veritabanı. Dosya belleğe alınmadan akış halinde `mysql` istemcisine verilir; ilerleme çubuğu okunan baytı gösterir, İptal ile durdurulur. Hata olursa hatalı satırın numarası ve metni gösterilir. MariaDB dökümünü MySQL'e (ya da tersini) yüklerken uyumsuz satırlar (`uca1400` / `0900` karşılaştırmaları, `/*!80016 ... */`, MariaDB sandbox satırı, `GTID_PURGED`) yolda düzeltilir.

### Denetle ve onar

Menü > Veritabanı > **Denetle ve onar**. XAMPP'taki "MySQL shutdown unexpectedly" sorununun çoğu bozuk Aria/MyISAM tablolarından (çoğunlukla `mysql.*` sistem tabloları) gelir. Önce sistem tabloları, sonra tüm veritabanları `mysqlcheck` / `mariadb-check` ile denetlenir; bozuk MyISAM/Aria tabloları onarılır. Sonuç: "N tablo denetlendi, M onarıldı, K kurtarma modu gerektiriyor". InnoDB tabloları bu yolla onarılamaz; aynı pencereden kurtarma modu başlatılabilir (iki adımlı onay).

MariaDB Aria günlükleri yüzünden hiç başlamıyorsa çevrimdışı onarım önerilir: `aria_log.*`, `aria_log_control` ve `mysql.*` Aria tabloları önce `backup\mysql\aria-<tarih>` klasörüne kopyalanır, tablolar `aria_chk -r` ile onarılır, günlük dosyaları ancak kopya doğrulandıktan sonra kaldırılır ve sunucu başlatılır. Veritabanı beklenmedik kapandığında ve hata günlüğünde çökme/bozulma izi olduğunda hata metni bu menüyü önerir.

### Sürüm değiştirirken veritabanlarını taşıma

Her motor + ana sürüm kendi veri klasörünü kullanır. Yeni bir sürüme geçince (Paketler > "Bu sürümü kullan", servis kartındaki sürüm listesi ya da Ayarlar'daki motor seçimi) yeni sürümün klasörü yoksa ve eskisinde veritabanı varsa Portly sorar: **Veritabanlarımı taşı**, **Boş başlat** ya da İptal.

- Taşımada önce eski sürüm bir kez başlatılır (düzgün kapanmamışsa kendi kurtarmasını yapar), her şey `backup\mysql\pre-upgrade-*.sql` olarak yedeklenir ve eski sunucu düzgünce durdurulur.
- **Aynı motor, yükseltme** (MySQL 5.7 → 8.0 → 8.4 → 9.x, MariaDB 10.x → 11.x): veri klasörü yeni sürüm için **kopyalanır** (taşınmaz) ve yeni sunucu başlatılır. MySQL 8.0.16+ kendini başlarken yükseltir; MariaDB'de ardından `mariadb-upgrade` çalışır; 5.7 → 8.0'da önce `mysqlcheck --check-upgrade` çalıştırılır. Seri atlayan MySQL geçişleri (5.7 → 8.4, 8.0 → 9.x) döküm yoluyla yapılır.
- **Motor değişimi (MySQL ↔ MariaDB) ya da eski sürüme dönüş:** dosyalar asla kopyalanmaz; veritabanları eski sunucudan dökülüp yenisine yüklenir (aynı uyumluluk düzeltmeleriyle). root dışındaki kullanıcılar taşınmaz.
- Eski veri klasörüne dokunulmaz; yeri sonuç penceresinde yazar. Yeni sürüm başlamazsa eski sürüm yeniden etkin olur, yarım kopya `.failed-<tarih>` adıyla kenara alınır.
- Her başarılı açılışta veri klasörüne `portly-server.txt` (son kullanan sürüm) yazılır. Daha yeni bir sürümün kullandığı klasörü eski bir sürümün açması engellenir.

### WSL'den erişim (Modern sürüm, isteğe bağlı)

Ayarlar > Özellikler > **WSL**, sonra Menü > Veritabanı > **WSL'den erişim...**. WSL2'nin varsayılan NAT ağında Linux içinden Windows'taki `localhost`'a ulaşılamaz.

- `.wslconfig` içinde `networkingMode=mirrored` varsa bir şey değişmez: WSL içinden `127.0.0.1` kullanılır.
- Yoksa Portly `vEthernet (WSL)` bağdaştırıcısının adresini bulur, MySQL'in `bind-address` listesine ekler (MySQL 8.0.13+ / MariaDB 10.11+; eski sürümde "Yerel ağdan erişim" önerilir), `portly_wsl'@'<alt ağ>.%'` kullanıcısını rastgele şifreyle oluşturur (şifre ayarlarda DPAPI ile şifreli), yalnızca o alt ağ ve MySQL portu için Windows Güvenlik Duvarı kuralı ekler (yönetici izni; neler yapılacağı önce gösterilir) ve kopyalanabilir bağlantı bilgisini gösterir. WSL içinden sunucu adresi varsayılan ağ geçididir: `ip route show default | awk '{print $3}'`.
- WSL alt ağı yeniden başlatmadan sonra değişebilir; dinleme adresi her açılışta güncellenir, kullanıcı ve kural için pencerede **Güncelle** düğmesi çıkar. **Kapat** (ya da özelliği kapatmak) kullanıcıyı, dinleme adresini ve kuralı kaldırır.

## Sık sorunlara karşı önlemler

Ayrıntılı araştırma: [05-yaygin-sorunlar.md](05-yaygin-sorunlar.md).

- **Sistem denetimi** (ilk açılışta ve Menü > Araçlar): OneDrive içindeki klasörler, İngilizce dışı harf içeren yollar, kapalı uzun yol desteği (tek tıkla açılır), `PHPRC` değişkeni, PATH'te başka bir PHP.
- **root şifresini unuttum:** Menü > Veritabanı > root şifresini sıfırla. Sunucu bir kez `--init-file` ile başlar, şifre boşalır.
- **Çökme / elektrik kesintisi:** önce Menü > Veritabanı > Denetle ve onar; InnoDB için Kurtarma modunda başlat (`innodb_force_recovery=1`), yedek alın, sonra normal başlatın.
- **Yavaşlık:** Menü > Araçlar > Windows Defender istisnası (isteğe bağlı, yönetici izni ister).
- **hosts engeli:** Antivirüs hosts dosyasını geri alırsa Portly uyarır; çözüm olarak `localhost` alan adı sonu önerilir.
- **Eski uygulamalar:** Ayarlar > MySQL > esnek SQL modu.
- **Ağdan erişim:** Ayarlar'da açılınca Windows Güvenlik Duvarı kuralı da sorulur.

## Başka bilgisayara taşıma

Ayarlar > **Ayarları dışa aktar** tek bir `.zip` oluşturur: ayarlar, site başına seçenekler (`usr\sites.ini`), kendi şablonlarınız (`usr\templates`) ve kurulu paket listesi (sürümler ve etkin olanı). Yeni bilgisayarda Ayarlar > **Ayarları içe aktar** ile yüklenir; eksik paketler Paketler penceresinde seçili gelir, "Seçilenleri kur" ile kurulur.

Dosyada **olmayanlar:** projeler (klasörü kopyalayın), veritabanları ("Tüm veritabanlarını yedekle" ile alıp yükleyin), MySQL root şifresi ve yerel sertifika anahtarı (bilgisayara bağlı, güvenlik için taşınmaz). Eski proje klasörü yeni bilgisayarda yoksa şimdiki klasör korunur.

Dosya biçimi platformdan bağımsızdır (`manifest.ini`: biçim sürümü ve platform; paketler tür + sürüm numarasıyla, proje klasörü kullanıcı klasörüne göre `~/...` olarak da saklanır). Böylece macOS / Linux sürümleri geldiğinde aynı dosya orada da açılabilecek; yalnız Windows'a özgü ayarlar (terminal, PATH, otomatik başlatma) atlanır.

## Ana ekrandaki servisler

Servisler sayfasında **Servisleri seç** ile ana ekranda görünecek servisler işaretlenir. Gizlenen servis durdurulur ve "Hepsini başlat" onu başlatmaz. PostgreSQL, MongoDB ve Memcached kurulana kadar varsayılan olarak gizlidir.

## Windows'ta zaten kurulu programlar

VS Code, Notepad++, Git, HeidiSQL, DBeaver, Beekeeper Studio, DbGate ve DB Browser for SQLite bilgisayarda normal yolla kuruluysa Portly onları kullanır; Paketler penceresinde yeşil "Windows'ta kurulu" satırı görünür ve yeniden indirmek gerekmez. İsterseniz taşınabilir sürümü yine de kurabilirsiniz; o zaman Portly'nin kendi kopyası önceliklidir.

## PHP eklentileri (redis, imagick, apcu ...)

PECL artık Windows için kolay bir indirme sayfası sunmuyor; yanlış DLL (başka PHP dalı, TS/NTS, VS16/VS17, x86) PHP'nin açılmasını engeller. **PHP menüsü > PHP eklentileri...** (ya da Paketler penceresindeki aynı düğme) seçilen PHP sürümünün derlemesini (`php -i`: NTS, derleyici, x64, API sürümü) gösterir ve iki liste sunar:

- **İndirilebilir:** `manifest/extensions.json` içindeki, tam o PHP derlemesine uyan ve SHA-256 değeri kayıtlı eklentiler (redis, apcu, imagick, mongodb, igbinary, memcached, xhprof, sqlsrv, pdo_sqlsrv, yaml, ast, pcov, msgpack, mailparse, ssh2, zstd, oauth). Kaynak: `downloads.php.net/~windows/pecl/releases`. Liste Windows 10/11 sürümünde pencere açılınca güncellenir; Legacy sürümü yalnız gömülü listeyi kullanır.
- **PHP ile gelenler:** `ext` klasöründeki eklentiler; Aç/Kapat php.ini'yi değiştirir.

**Kur** düğmesi: indirir, SHA-256'yı denetler, DLL'i `ext` klasörüne, gereken ek DLL'leri (ör. imagick için ImageMagick `CORE_RL_*`/`IM_MOD_RL_*`) `php.exe`'nin yanına koyar (PHP'nin kendi DLL'lerinin üzerine yazmaz), php.ini'de açar ve `php -m` ile yüklendiğini denetler. PHP açılmazsa php.ini ve kopyalanan dosyalar geri alınır, PHP'nin hata mesajı gösterilir. O PHP sürümü çalışıyorsa web sunucusu ve PHP yeniden başlatılır. Kurulan dosyalar `<php>\portly-extensions.ini` dosyasına yazılır; **Kaldır** yalnız bunları siler.

Yeni bir PHP sürümü kurulduğunda ya da güncellendiğinde Portly, önceki sürüme kurduğu eklentileri yeni sürüme de kurmayı önerir ve o sürüm için derlemesi olmayanları söyler.

Notlar: sqlsrv/pdo_sqlsrv için Windows'ta Microsoft ODBC Driver 17/18 kurulu olmalı; imagick için Visual C++ 2015-2022 x64 çalışma zamanı gerekir (Portly zaten önerir). excimer ve uuid'in Windows derlemesi yoktur.

## Terminal her zaman projenin sürümlerini kullanır (shim'ler)

UI'da PHP sürümü değiştirilince açık terminaller eski PHP'yi kullanmaya devam ediyordu. Artık Portly `bin\shims` klasörüne sabit komutlar yazar: `php`, `composer`, `wp`, `node`, `npm`, `npx` (cmd/PowerShell için `.cmd`, Git Bash için uzantısız bash betikleri). Klasör hiç değişmez; komut her çalıştığında içinde bulunulan klasöre göre sürümü seçer:

1. Bulunulan klasörde ya da bir üst klasörde `.php-version` dosyası (`8.3`, `8.3.4` gibi)
2. Klasörü içeren Portly sitesinin PHP seçimi (sites.ini `php=`)
3. O sitenin `composer.json` içindeki `require.php` kısıtı (`^8.1`, `~8.2.0`, `>=8.1 <8.4` ...): kısıta uyan en yeni kurulu sürüm
4. Portly'nin varsayılan PHP'si

`.php-version` dosyasındaki sürüm kurulu değilse komut çalışmaz, hangi sürümün eksik olduğunu ve Paketler > PHP'den kurulacağını söyleyen tek satırlık bir mesaj yazar.

Shim'ler hızlıdır: her çağrıda .NET başlatılmaz, Portly'nin site/sürüm/ayar değişince yazdığı küçük harita dosyalarını (`bin\shims\map`) okur. Portly'nin Terminal düğmesi shim klasörünü PATH'in başına koyar. Ayarlar'daki **PATH'e ekle** açıksa kullanıcı PATH'ine de önce shim klasörü, arkasında etkin paket klasörleri yazılır (php.exe/node.exe'yi doğrudan arayan editörler için yedek) ve yeni açılan pencereler oturumu kapatmadan görür.

Bilinen sınırlar: proje yolunda `!` ya da konsol kod sayfasında olmayan harfler varsa cmd tarafında 2. ve 3. adım atlanır (varsayılan PHP kullanılır; `.php-version` yine çalışır). `composer.json` kısıtı yalnız Portly sitelerinde (www altındaki projeler) hesaplanır. `.cmd` komutunu Ctrl+C ile durdurunca Windows "Toplu iş sonlandırılsın mı?" diye sorabilir. Özellik Ayarlar > Özellikler'den kapatılabilir; o zaman `bin\shims` silinir.

## Birden fazla Node.js sürümü

Ayarlar > Özellikler > **Birden fazla Node.js sürümü** açıkken `node`, `npm`, `npx` shim'leri sürümü şuna göre seçer: bulunulan ya da üst klasördeki `.nvmrc` / `.node-version` (`22`, `v22.11.0`, `22.x`, `lts/*`, `lts/jod`, `node` ...), yoksa Portly sitesinin `package.json` içindeki `engines.node` kısıtı, yoksa etkin Node.js. Uyan sürüm kurulu değilse sürümü ve Paketler > Node.js'ten kurulacağını söyleyen tek satırlık mesaj yazılır. Node sürümleri Paketler penceresinden yan yana kurulur.

npm'in global paketleri her ana sürüm için ayrı klasöre gider (`usr\npm\node22` ...); shim bu klasörü PATH'e ekler, böylece `npm i -g` ile kurulan araçlar kendi Node sürümüyle çalışmaya devam eder. Özellik kapalıyken shim'ler hep etkin Node.js'i kullanır ve npm'in global klasörüne dokunmaz.
## Yeni sürüm bildirimleri

Modern sürümde Portly, günün ilk açılışında (Ayarlar'da "Güncellemeleri otomatik denetle" açıksa) ve **Menü > Güncellemeleri denetle** ile paket kataloğunu yeniler. Kurulu her paket türü için iki şeye bakar:

- **Yeni dal**: kurulu en yeni sürümden daha yeni bir seri çıktıysa (ör. PHP 8.5 kullanıyorsunuz, PHP 8.6 çıktı) Servisler sayfasının üstünde "PHP 8.6.0 çıktı" kartı görünür. **Kur ve kullan** indirir, kurar ve varsayılan sürüm yapar (web sunucusu yeniden yüklenir). Ön sürümler (RC, beta) önerilmez. MySQL, MariaDB, Node.js ve MongoDB'de yalnızca uzun destekli (LTS) seriler önerilir; ör. MySQL 8.4 kullanana 9.7 önerilir, 26.x Innovation önerilmez. Nginx'te mainline değil stable önerilir.
- **Yama**: aynı serinin daha yeni yapısı (PHP 8.4.26 → 8.4.27). Birden çok yama varsa **Tümünü güncelle (3)** ile hepsi birlikte kurulur; php.ini ve ayarlar yeni sürüme taşınır.

Veritabanlarında (MySQL, MariaDB, PostgreSQL, MongoDB) yeni seri yalnızca kurulur, geçiş sizde kalır: veri klasörü seriye özgü olduğu için geçişi sürüm listesinden, verileri güvenle taşıyan normal geçişle yaparsınız.

**Sonra** kartı bu oturum için gizler, **Bu sürümü atla** o sürümü bir daha göstermez (her paket türü için ayrı hatırlanır). Bildirimler açılışta pencere açmaz; yalnızca kart görünür.

Yama güncellemesi bitince Portly eski yapıyı kaldırmayı önerir (bir site o PHP sürümünü seçmişse ya da çalışıyorsa önermez).

## Destek bitişi ve güvenlik uyarıları

Portly, kurulu sürümleri `security.json` dosyasındaki bilgilerle karşılaştırır (Legacy sürüm yalnızca exe içindeki kopyayı kullanır):

- Serinin desteği bittiyse servis kartında turuncu **Desteği bitti** etiketi ve Paketler penceresinde "… tarihinden beri destek yok" yazısı çıkar.
- Bilinen bir güvenlik açığı varsa kırmızı **Güvenlik düzeltmesi** etiketi ve "Güvenlik düzeltmesi var: 8.3.31 (CVE-…)" yazısı çıkar. Etikete tıklayınca aynı serinin düzeltilmiş sürümüne tek tıkla geçilir.
- Aynı dosya bazı güvenlik ayarlarını tüm php.ini dosyalarına uygulayabilir; ama yalnızca Portly'nin içindeki sabit listede olan, PHP'yi daha sıkı yapan ayarlar (`session.upload_progress.enabled = Off`, `allow_url_include = Off`, `expose_php = Off` gibi). Listede olmayan her şey yok sayılır.

## Antivirüs ve Akıllı Uygulama Denetimi

- **Akıllı Uygulama Denetimi (Smart App Control)** açık ya da değerlendirme modundaysa Sistem denetimi bunu bir kez bildirir: imzasız PHP eklentilerini veya Apache modüllerini engelleyebilir. Bir servis başlamazsa Portly günlükleri ve Windows Kod Bütünlüğü kaydını okuyup engellenen ya da bulunamayan DLL'in adını hata satırında gösterir.
- Portly her paketi kurarken dosya listesini (`.portly-files`: yol, boyut, .exe/.dll için SHA-256) paket klasörüne yazar. Her açılışta etkin sürümlerin .exe/.dll dosyalarının yerinde olup olmadığına hızlıca bakar; eksik varsa "Antivirüsünüz büyük olasılıkla PHP 8.5.12 sürümünün N dosyasını sildi" penceresi açılır.
- **Araçlar > Paketleri doğrula (antivirüs / karantina)**: tüm paketlerin tam denetimi (dosya varlığı + SHA-256), **Onar** (aynı sürümü yeniden indirip yalnızca eksik/değişmiş dosyaları geri koyar; php.ini ve ayarlarınız kalır), **Defender istisnası ekle** (yönetici onayı ister), **Defender geçmişi** (Windows Defender'ın Portly klasöründeki bulguları ve son 7 günde engellenen dosyalar) ve **Defender ile tara** (Portly klasörünü `MpCmdRun.exe -Scan -ScanType 3` ile tarar).
- Bu özellikten önce kurulmuş sürümlerin dosya listesi yoktur; denetimde atlanır. Yeniden kurunca liste oluşur.

## Kullanılmayan sürümleri kaldırma

**Paketler > Kullanılmayanları kaldır…** etkin olmayan, hiçbir sitenin seçmediği (`sites.ini` içindeki `php=`) sürümleri boyutlarıyla listeler. İşaretlediğiniz sürümler kaldırılır; çalışan bir programı olan sürüme dokunulmaz. Veritabanı verileri (`data\`) ve projeler silinmez.

## Vekil sunucu (proxy)

**Ayarlar > Genel > Vekil sunucu (proxy)** tüm indirmeler için geçerlidir:

- **Sistem** (varsayılan): Windows'un proxy ayarı (Internet Seçenekleri, PAC betiği) ve oturum kullanıcısının kimliği.
- **Yok**: doğrudan bağlantı.
- **Elle**: `sunucu:port`, isteğe bağlı kullanıcı ve şifre (şifre Windows DPAPI ile şifrelenip saklanır). Bu modda Portly terminali `HTTP_PROXY`, `HTTPS_PROXY` ve `NO_PROXY` değişkenlerini de ayarlar; composer, npm, git ve pip proxy'yi kullanır.

**Bağlantıyı dene** paket kataloğunu indirmeyi dener ve sorunu sade dille söyler: DNS, proxy adresi, proxy şifresi (407), TLS (HTTPS'i inceleyen antivirüs/şirket proxy'si) ya da HTTP hatası.

## Sessiz kurulum ve paket yöneticileri

```
Portly-<sürüm>-win10-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="D:\Portly"
```

Kaldırırken paketler, veritabanları, projeler ve ayarlar varsayılan olarak korunur; hepsini silmek için `unins000.exe /VERYSILENT /PURGE` kullanın (pencereli kaldırmada sorulur, varsayılan cevap "koru"dur). winget (`winget install ErdemSoftware.Portly`) ve Scoop paketleri yayın sürecinde hazırlanır (bkz. `docs/03-yayinlama.md`).
