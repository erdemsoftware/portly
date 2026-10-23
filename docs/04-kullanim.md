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

## Sık sorunlara karşı önlemler

Ayrıntılı araştırma: [05-yaygin-sorunlar.md](05-yaygin-sorunlar.md).

- **Sistem denetimi** (ilk açılışta ve Menü > Araçlar): OneDrive içindeki klasörler, İngilizce dışı harf içeren yollar, kapalı uzun yol desteği (tek tıkla açılır), `PHPRC` değişkeni, PATH'te başka bir PHP.
- **root şifresini unuttum:** Menü > Veritabanı > root şifresini sıfırla. Sunucu bir kez `--init-file` ile başlar, şifre boşalır.
- **Çökme / elektrik kesintisi:** Menü > Veritabanı > Kurtarma modunda başlat (`innodb_force_recovery=1`), yedek alın, sonra normal başlatın.
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

## Arka plan işleri (kuyruk, zamanlayıcı, Vite)

Ayarlar > Özellikler > **Arka plan işleri** (varsayılan kapalı). Site kartı **⋯** > **Arka plan işleri...** penceresinde her site için işler eklenir, başlatılır, durdurulur ve günlüğü canlı izlenir. Hazır şablonlar:

| Şablon | Komut | Not |
|---|---|---|
| Laravel kuyruk işçisi | `php artisan queue:work --tries=3` | Kod değişince yeniden başlatın |
| Laravel zamanlayıcı | `php artisan schedule:run` | Sürekli süreç değildir; Portly her dakikanın başında bir kez çalıştırır (cron gibi) |
| Vite | `npm run dev` | Node.js paketi ve `npm install` gerekir |
| Laravel Reverb | `php artisan reverb:start` | |
| Özel komut | herhangi bir komut | |

- İş proje klasöründe `cmd.exe` ile çalışır. PATH'te önce **sitenin PHP sürümü**, sonra Portly'nin Node, Composer vb. araçları vardır. `PORTLY_ROOT`, `PORTLY_SITE`, `PORTLY_SITE_DIR`, `PORTLY_SITE_URL` değişkenleri tanımlıdır.
- İşler Portly'nin job object'i içindedir: Portly çökse bile Windows onları kapatır. "Tümünü durdur" ve çıkışta önce Ctrl+C gönderilir, 5 saniyede kapanmazsa süreç ağacı sonlandırılır.
- "Durursa yeniden başlasın" seçiliyse kapanan iş 1, 2, 4, 8, 16 sn beklemelerle yeniden başlar; bir dakikada 5 kereden fazla kapanırsa **hata** durumuna geçer.
- "Tümünü başlat ile başlasın" seçili işler "Tümünü başlat" (ve `portly start`) ile başlar. Çalışan iş sayısı site kartında yeşil rozetle görünür.
- Günlükler: `logs\jobs\<site>-<iş>.log` (5 MB'ta `.1` olarak döner).
- Tanımlar `usr\sites.ini` içinde durur: `job.queue=autostart,restart|php artisan queue:work`.
- **Laravel Horizon** `pcntl` eklentisi ister, bu eklenti Windows'ta yoktur. Yerelde kuyruk işçisini (`queue:work`) kullanın.

## Site günlükleri

Site kartı **⋯** > **Günlükler...** (özellik: "logs", varsayılan açık). Sitede olan sekmeler görünür:

| Sekme | Dosya |
|---|---|
| PHP hataları | `logs\php_errors.log`, yalnızca bu sitenin klasörünü ya da adresini içeren kayıtlar |
| Apache / Nginx hataları | `logs\apache-error.log` / `logs\nginx-error.log`, aynı süzgeçle |
| Laravel | `storage\logs\laravel*.log` içinde en yeni dosya (günlük kanal da olur) |
| WordPress | `wp-content\debug.log` |
| İş: … | arka plan işlerinin günlükleri |

PHP ve web sunucusu tek ortak dosyaya yazar (php-cgi'de havuz başına `error_log` yoktur; projeye `.user.ini` koymak istemedik), bu yüzden bu iki sekme süzülmüş görünür. Pencere son ~2 MB'ı gösterir, saniyede bir yeni satırları ekler, dosya temizlenince ya da döndürülünce baştan okur. **Süz** kutusu satırları metne göre süzer. **Temizle** dosyayı boşaltır (ortak dosyada bunun tüm siteleri etkilediği sorulur).

## Profilleyici (Xdebug)

Ayarlar > Özellikler > **dump() sunucusu ve profilleme** (varsayılan kapalı). Site kartı **⋯** > **Profilleyici...**:

1. **Aç**: sitenin PHP sürümünde Xdebug yoksa indirilir (sorulur) ve `xdebug.mode=debug,develop,profile`, `start_with_request=trigger` yapılır. Profil dosyaları `logs\profiles\cachegrind.out.*` olur.
2. **Bir isteği profille**: siteyi `?XDEBUG_TRIGGER=1` ile açar. Tetikleyicisiz istekler profillenmez.
3. Listeden bir profil seçilince en yavaş fonksiyonlar (toplam süre, kendi süresi, çağrı sayısı, yüzde) gösterilir. Dosyalar QCacheGrind / PhpStorm ile de açılabilir.
4. İş bitince **Kapat**. Xdebug'ı profilleyici açtıysa onu da kapatır (Xdebug her isteği yavaşlatır).

## Komut satırı: portly

Özellik: "cli" (varsayılan açık). Portly klasöründe `portly.cmd` oluşur; klasör PATH'teyse her terminalde çalışır.

```
portly status                      servislerin durumu, portlar, çalışan işler
portly start [servis]              hepsini ya da birini başlat (web, php, mysql, redis, mailpit, postgresql, mongodb, memcached)
portly stop [servis]
portly restart [servis]
portly sites                       siteler, adresler, PHP sürümleri
portly open <site>                 siteyi tarayıcıda aç
portly share <site> --yes          siteyi internette paylaş (--yes olmadan yalnızca ne olacağını söyler)
portly use php 8.3 [--site shop]   varsayılan ya da sitenin PHP sürümü ("8.3", "^8.2" ...)
portly db backup                   tüm veritabanlarını yedekle
portly version | help
```

Portly çalışıyorsa komut ona adlandırılmış kanal (named pipe, yalnızca aynı Windows kullanıcısı, ağdan erişilemez) ile gider; çalışmıyorsa önce sistem tepsisinde başlatılır. Çıkış kodu başarıda 0, hatada 1'dir.

**Betikler (hooks):** `usr\hooks\before-start.cmd`, `after-start.cmd`, `before-stop.cmd`, `after-stop.cmd` varsa "Tümünü başlat / durdur" çevresinde çalışır. Gizli çalışır, en çok 120 sn (`usr\portly.ini` → `[devworkflow] HookTimeoutSeconds`), çıktısı Günlük sayfasına yazılır. Değişkenler: `PORTLY_ROOT`, `PORTLY_EVENT`. Siteye özel betikler portly.yml'den gelir (aşağıda; ek değişken `PORTLY_SITE`, klasör sitenin klasörüdür).

## Proje dosyası: portly.yml

Özellik: "projectFile" (varsayılan açık). Proje köküne konan `portly.yml` projeyi ekipteki herkes için aynı şekilde kurar (Herd'ün `herd.yml` dosyası gibi). Biçim platformdan bağımsızdır; ileride macOS / Linux sürümleri de aynı dosyayı okur.

```yaml
version: 1
php: "8.3"                 # ya da "^8.2", ">=8.1 <8.4", "8.3.*"
node: "22"                 # bilgi amaçlı; etkin sürüm farklıysa uyarır
root: public               # site kökü (proje klasörüne göre)
domains: [shop.test, "*.shop.portly"]
services: [mysql, redis, mailpit]
database: { name: shop, collation: utf8mb4_turkish_ci }
jobs:
  - name: queue
    command: "php artisan queue:work"
    autostart: true        # varsayılan false
    restart: true          # varsayılan true
  - name: schedule
    command: "php artisan schedule:run"
    schedule: true         # her dakika bir kez
hooks:
  after-start: "php artisan migrate --force"
  before-stop: "php artisan queue:restart"
```

Desteklenen YAML alt kümesi: girintili eşlemeler ve listeler, `- ad: değer` liste öğeleri, tek satırda `[a, b]` ve `{ a: 1 }`, tırnaklı / tırnaksız metin, sayı, `true/false`, `null`, `#` açıklamaları. Çapa (`&`), takma ad (`*`), etiket (`!`), çok satırlı metin (`|`, `>`), sekme ile girinti ve birden çok belge satır numarasıyla reddedilir. Bilinmeyen anahtarlar uyarıyla atlanır.

Akış:
1. Portly uygulanmamış bir `portly.yml` görünce site kartında turuncu **portly.yml** rozeti çıkar (engellemez).
2. Rozete ya da **⋯** > **portly.yml'yi uygula...** seçeneğine tıklayınca neyin değişeceği tek tek listelenir.
3. Komutlar (jobs, hooks) ayrı bir bölümde gösterilir ve yalnızca **"Bu dosyadaki komutlara izin ver"** işaretlenirse kaydedilir. Bir depo dosyası hiçbir zaman sessizce komut çalıştırmaz; uygulama sırasında da komut çalışmaz, yalnızca kaydedilir.
4. Uygulanınca: PHP için koşula uyan en yeni kurulu sürüm seçilir (yoksa katalogdan uygun sürüm indirilir), ek alan adları eklenir, `root=` yazılır, servisler "Tümünü başlat"a eklenir, veritabanı oluşturulur (karşılaştırma verilmişse `ALTER DATABASE ... COLLATE`), işler ve betikler kaydedilir.
5. Dosyanın özeti `usr\sites.ini` içinde `projectfile=` olarak saklanır; dosya değişince rozet yeniden çıkar.

**⋯** > **portly.yml oluştur** sitenin şu anki ayarlarından dosyayı yazar.
