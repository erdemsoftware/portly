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
