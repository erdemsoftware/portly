<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Windows için hızlı, taşınabilir, açık kaynak yerel web geliştirme ortamı<br>
  <sub><a href="https://erdemsoftware.com">Erdem Software</a> · MIT · reklamsız, izlemesiz</sub>
</p>

<p align="center"><a href="README.md">English</a> · <b>Türkçe</b></p>
<p align="center"><sub>Uygulamanın kendisi 31 dil destekler (Arapça, Farsça ve Urduca sağdan sola).</sub></p>

---

Portly; Apache ya da Nginx, PHP, MySQL ya da MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer ve phpMyAdmin'i tek bir klasörde çalıştırır. Kurulum gerekmez, Windows servisi eklemez, kayıt defterini kirletmez. Klasörü bir USB belleğe kopyalasanız bile çalışır.

## Neden Portly?

| | Laragon | **Portly** |
|---|---|---|
| Lisans | Kapalı kaynak, ticari kullanım ücretli | **MIT, tamamen ücretsiz, reklamsız** |
| Uygulama güncellemesi | Yeni kurulum dosyası | **Uygulama içinden tek tık** (SHA-256 doğrulamalı) |
| Paket listesi | Yerel `packages.conf` | **Uzaktan güncellenen katalog** + windows.php.net'ten canlı PHP sürümleri |
| Site başına PHP sürümü | Yok | **Var** (her PHP sürümü kendi FastCGI portunda) |
| HTTPS | Tek sertifika, her projede yeniden | **Yerel kök sertifika** bir kez güvenilir yapılır, her siteye ayrı sertifika |
| Ağ güvenliği | Tüm arayüzleri dinler | **Varsayılan 127.0.0.1** (güvenlik duvarı sorusu yok) |
| PHP güvenlik ayarları | Varsayılan | `session.upload_progress` ve `allow_url_include` **kapalı** |
| Çökme güvenliği | — | Portly kapanırsa servisleri Windows kapatır (job object) |
| Port çakışması | Hata mesajı | Portu tutan programı gösterir; **tek tıkla boş porta geç** ya da o programı kapat |
| Paylaşım | ngrok (hesap gerekir) | **Yerel ağ** (aynı Wi-Fi'deki telefon / tablet, QR kod, https, şifre) ve Cloudflare ile **internet**: hesapsız rastgele adres ya da **kendi alan adınızda sabit adres** |
| Geçiş | — | **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP**'tan projeler ve veritabanları (Herd ve Local'dan projeler) |
| Veritabanları | MySQL | **MySQL ya da MariaDB, PostgreSQL, MongoDB, SQLite**; Redis ve Memcached |
| Veritabanı araçları | HeidiSQL | **phpMyAdmin** (root şifresizken giriş yapılmış açılır), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Elle kurulum | **Tek tıkla WordPress** (WP-CLI, yönetici kullanıcısı hazır) |
| Dil | 34 dil (txt) | **31 dil**, JSON, katkıya açık |
| Windows 7 / 8 / 8.1 | Belirsiz | **Ayrı Legacy sürümü** |

## Özellikler

- **Projeler**: `www` içindeki her klasör `https://ad.portly` olur; Laravel gibi `public` klasörleri kendiliğinden bulunur. Grup klasörleri ve alt projeler (`blog.magaza.portly`); Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular ve Astro şablonları. Her kartta projenin neyle yazıldığı ve veritabanı görünür.
- **Proje paylaşımı**
  - *Yerel ağ*: telefonda QR kodla açın. https ile çalışır; telefondaki rehber Portly sertifikasını iPhone ve Android'e bir kez kurdurur. İsteğe bağlı şifre, süre sınırı, Portly ile başlatma, bağlanan cihazların listesi; telefonda oluşan PHP hataları Portly'nin günlüğüne düşer.
  - *İnternet*: ücretsiz Cloudflare hızlı tüneli (hesap gerekmez) ya da Cloudflare hesabınızla `demo.ornek.com` gibi kendi alan adınız. Aynı şifre, süre sınırı ve cihaz listesi.
- **Site başına PHP sürümü**, tek tıkla **PHP eklentileri** (17 PECL eklentisi, her PHP sürümüne uygun ve doğrulanmış), **Xdebug**, projenin PHP / Node sürümünü izleyen terminaller (`.php-version`, `composer.json`, `.nvmrc`).
- **Servisler**: her serviste asma kilitle seçilen Hepsini başlat / Hepsini durdur, tek kartta MySQL / MariaDB, isteğe bağlı Tomcat + Java.
- **Güncelleme bildirimi** ("PHP 8.6 çıktı" → tek tıkla indir ve geç), destek bitişi ve güvenlik açığı uyarıları.
- **Veritabanı güvenliği**: denetle ve onar, MySQL / MariaDB sürümleri arasında güvenli taşıma, büyük SQL içe aktarma, otomatik yedek, unutulan root şifresini sıfırlama, InnoDB kurtarma modu.
- **Geliştirici araçları**: arka plan işleri (kuyruk, zamanlayıcı, Vite), site günlükleri, profil çıkarma, kancalarıyla `portly` komut satırı, `portly.yml` proje dosyası, SSH ile sunucuya gönder / sunucudan çek.
- **Siteler**: ek ve joker alan adları, kendi sertifikanız, arşivleme, SSL onarma, Firefox / Java sertifika güveni, **Caddy** ve **FrankenPHP** (Octane).
- **Sorun önleme**: OneDrive klasörü, Türkçe karakterli ve uzun yollar, ikinci bir PHP ya da PHPRC, hosts dosyasını engelleyen antivirüs, karantina denetimi.
- Windows'u izleyen açık / koyu temalı modern arayüz, 4K ekran ve ekran okuyucu desteği. Az kullanılan özellikler Ayarlar > Özellikler'den açılır.

## Sürümler

| İndirme | Windows | Güncelleme | Paketler |
|---|---|---|---|
| `Portly-x.y.z-win10.zip` | 10 / 11 (64 bit) | **Var**: uygulama + paket kataloğu | En güncel: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64 bit) | **Yok** (sabit katalog) | Windows 7'de çalışan son sürümler: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Yakında.** Ayar dosyası şimdiden platformdan bağımsız. Desteklemek için yıldız verin ya da bağış yapın. |

Legacy sürüm için .NET Framework 4.8 gerekir (Windows 10 / 11'de zaten yüklüdür).

## Hızlı başlangıç

1. [Sürümler](https://github.com/erdemsoftware/portly/releases) sayfasından zip'i indirip `C:\Portly` gibi **boşluksuz** bir klasöre açın.
2. `Portly.exe`'yi çalıştırın. İlk açılışta önerilen paketler seçili gelir: **Seçilenleri kur**'a tıklayın.
3. **Hepsini başlat**'a tıklayın.
4. `www` içinde bir klasör açın: `www\magaza` otomatik olarak `https://magaza.portly` olur.

`http://localhost` adresinde tüm siteleri, Adminer'ı, Mailpit'i ve `phpinfo()`'yu gösteren bir pano açılır. Ayrıntılar: [docs/04-kullanim.md](docs/04-kullanim.md).

```
Portly\
├─ Portly.exe            uygulama
├─ lang\  templates\     diller ve yapılandırma şablonları (güncellemeyle yenilenir)
├─ bin\                  paketler: bin\php\php-8.5.12-..., bin\mysql\mysql-8.4.11-winx64 ...
├─ etc\                  üretilen yapılandırma (her başlatmada yeniden yazılır)
├─ usr\                  ayarlarınız, kendi şablonlarınız, SSL otoritesi (güncellemede dokunulmaz)
├─ data\                 veritabanları (her MySQL serisine ayrı klasör)
├─ www\                  projeleriniz (Ayarlar'dan başka klasör seçilebilir)
└─ logs\  tmp\  backup\
```

Bir yapılandırma şablonunu değiştirmek için `templates\` içindeki dosyayı `usr\templates\` içine kopyalayıp düzenleyin; güncellemeler sizin kopyanıza dokunmaz.

## Kaynaktan derleme

Gereken: .NET SDK 6 ya da daha yenisi (uygulama .NET Framework 4.8 hedefler; Windows 7 SP1 – 11'de ek kurulum gerekmez). Inno Setup kuruluysa `build.ps1` kurulum dosyalarını da üretir.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # iki sürüm -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # yalnız Windows 10/11 sürümü
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # test portlarında uçtan uca test
```

Mimari: [docs/02-mimari.md](docs/02-mimari.md), yayınlama: [docs/03-yayinlama.md](docs/03-yayinlama.md).

## Çeviri

Dil dosyaları [`src/Portly/lang`](src/Portly/lang) klasöründedir. `en.json`'u kopyalayın, değerleri çevirin, `{0}` yer tutucularını koruyun ve pull request gönderin. Eksik anahtarlarda İngilizce gösterilir. Bkz. [CONTRIBUTING.md](CONTRIBUTING.md).

## Gizlilik

Portly'de telemetri, kullanım istatistiği ve hesap yoktur. Başka sistemlere yalnız sizin başlattığınız ya da açtığınız işler için bağlanır:

- **Güncelleme denetimi** (varsayılan açık, Ayarlar'dan kapatılabilir): paket kataloğunu ve sürüm bilgisini bu GitHub deposundan indirir. Sizinle ya da projelerinizle ilgili hiçbir şey gönderilmez.
- **Paket kurulumu**: paketi resmî kaynağından indirir (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Projeyi internette paylaşma**: Cloudflare'in `cloudflared` programını çalıştırır; ziyaretçiler sitenize Cloudflare üzerinden ulaşır. Yerel ağ paylaşımı ağınızın dışına çıkmaz.

Portly sistem ayarlarını yalnız sorarak değiştirir: hosts dosyası ve yerel sertifika otoritesine güven için onay istenir (Windows UAC).

**Kaldırma**: taşınabilir zip sürümü klasörü silinerek kaldırılır; kurulum sürümü Windows Ayarlar > Uygulamalar'dan kaldırılır. Güvenilen Portly sertifika otoritesini de kaldırmak için `certmgr.msc` > Güvenilen Kök Sertifika Yetkilileri > Sertifikalar'da "Portly Local CA"yı silin.

## Kod imzalama politikası

Windows sürümleri bu depodan GitHub Actions ile derlenir. SignPath Foundation üzerinden kod imzalama için başvuru yapıldı; o zamana kadar sürümler imzasızdır ve her sürümle SHA-256 özetleri yayınlanır.

Ekip rolleri:
- Yazarlar / commit yetkilisi: [Erdem Software](https://github.com/erdemsoftware)
- İnceleyenler: [Erdem Software](https://github.com/erdemsoftware)
- Onaylayanlar (sürüm imzalama): [Erdem Software](https://github.com/erdemsoftware)

## Bağış

Portly ücretsizdir ve öyle kalacak: reklam yok, ücretli sürüm yok, izleme yok. İşinizi kolaylaştırdıysa geliştirmeye destek olabilirsiniz.

- Kart: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID: **50018297**
- Kripto (USDT: TRC-20, BEP-20, Polygon, Solana, Ethereum): uygulamada Menü > Bağış

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare ve diğer araçlar resmî kaynaklarından indirilir ve sahiplerine aittir. Portly'nin Laragon ile bir bağlantısı yoktur.</sub>
