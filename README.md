<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Fast, portable, open-source local web development environment for Windows<br>
  Windows için hızlı, taşınabilir, açık kaynak yerel web geliştirme ortamı<br>
  <sub>by <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · no ads, no tracking</sub>
</p>

---

**[Türkçe](#türkçe)** · **[English](#english)** · **[Donate / Bağış](#donate)**

## Türkçe

Portly; Apache ya da Nginx, PHP, MySQL, Node.js, Redis, Mailpit, Composer ve Adminer'ı tek bir klasörde çalıştırır. Kurulum gerekmez, Windows servisi eklemez, kayıt defterini kirletmez. Klasörü bir USB belleğe kopyalasanız bile çalışır.

### Neden Portly?

| | Laragon | **Portly** |
|---|---|---|
| Lisans | Kapalı kaynak, ticari kullanım ücretli | **MIT, tamamen ücretsiz, reklamsız** |
| Uygulama güncellemesi | Elle kurulum dosyası | **Uygulama içinden tek tık** (SHA-256 doğrulamalı) |
| Paket listesi | Yerel `packages.conf` | **Uzaktan güncellenen katalog** + windows.php.net'ten canlı PHP sürümleri |
| Site başına PHP sürümü | Yok | **Var** (her PHP sürümü kendi FastCGI portunda) |
| HTTPS | Tek sertifika, her projede yeniden | **Yerel kök sertifika** bir kez güvenilir yapılır, her siteye ayrı sertifika |
| hosts dosyası | node.js ile | **Kendi küçük UAC yardımcısı**, sadece değişiklik olunca |
| Ağ güvenliği | Tüm arayüzleri dinler | **Varsayılan 127.0.0.1** (güvenlik duvarı sorusu yok) |
| PHP güvenlik ayarları | Varsayılan | `session.upload_progress` ve `allow_url_include` **kapalı** |
| Çökme güvenliği | — | Portly kapanırsa servisler Windows tarafından kapatılır (job object) |
| Port çakışması | Hata mesajı | Portu tutan program ve yolu gösterilir; **tek tıkla boş porta geç** ya da o programı kapat; artık süreçler kendiliğinden temizlenir |
| Arayüz | Klasik | Kenar çubuklu modern arayüz, **açık/koyu tema** (Windows'u izler) |
| Dil | 34 dil (txt) | JSON, **31 dil** (Arapça, Farsça, Urduca sağdan sola), GitHub'dan katkıya açık |
| Windows 7 / 8 / 8.1 | Belirsiz | **Ayrı Legacy sürümü** |

### Sürümler

| Sürüm | Windows | Güncelleme | Paketler |
|---|---|---|---|
| `Portly-x.y.z-win10.zip` | 10 / 11 (64 bit) | **Var**: uygulama + paket kataloğu | En güncel: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4.69, Node 24 LTS... |
| `Portly-x.y.z-win7.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64 bit) | **Yok** (sabit katalog) | Win7'de çalışan son sürümler: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |

Legacy sürüm için .NET Framework 4.8 gerekir (Windows 10/11'de zaten yüklüdür).

### Hızlı başlangıç

1. [Sürümler](https://github.com/erdemsoftware/portly/releases) sayfasından zip'i indirin ve `C:\Portly` gibi **boşluksuz** bir klasöre açın.
2. `Portly.exe`'yi çalıştırın. İlk açılışta önerilen paketler seçili gelir, **Seçilenleri kur**'a tıklayın.
3. **Hepsini başlat**'a tıklayın.
4. `www` klasöründe bir klasör açın: `www\magaza` otomatik olarak `https://magaza.portly` olur. Laravel gibi `public` klasörü olan projeler kendiliğinden doğru köke bağlanır.

Gruplar, elle sürüm ekleme ve HTTPS için: [docs/04-kullanim.md](docs/04-kullanim.md).

`http://localhost` adresinde tüm siteleri, Adminer'ı, Mailpit'i ve `phpinfo()`'yu gösteren bir pano açılır.

### Klasör yapısı

```
Portly\
├─ Portly.exe            uygulama (~300 KB)
├─ lang\  templates\     diller ve yapılandırma şablonları (güncellemeyle yenilenir)
├─ bin\                  paketler: bin\php\php-8.5.12-..., bin\mysql\mysql-8.4.11-winx64 ...
├─ etc\                  üretilen yapılandırma (her başlatmada yeniden yazılır)
├─ usr\                  ayarlarınız, kendi şablonlarınız, SSL otoritesi (güncellemede dokunulmaz)
├─ data\                 veritabanları (her MySQL serisine ayrı klasör)
├─ www\                  projeleriniz (Ayarlar'dan başka klasör seçilebilir)
└─ logs\  tmp\  backup\
```

Bir yapılandırma şablonunu değiştirmek için `templates\` içindeki dosyayı `usr\templates\` içine kopyalayıp düzenleyin. Güncellemeler sizin kopyanıza dokunmaz.

---

## English

Portly runs Apache or Nginx, PHP, MySQL, Node.js, Redis, Mailpit, Composer and Adminer from a single folder. No installer, no Windows services, no registry clutter. It even works from a USB stick.

**Highlights**

- One-click **self update** (SHA-256 verified) and a **remotely updated package catalog**; new PHP releases show up automatically from windows.php.net.
- **Per-site PHP version**: every PHP version runs as its own FastCGI pool.
- **HTTPS without warnings**: a local root CA is trusted once, every site gets its own certificate.
- Every folder in your projects folder becomes `https://name.portly`; Laravel-style `public` folders are detected.
- Listens on **127.0.0.1 only** by default, PHP hardened (`session.upload_progress`, `allow_url_include` off).
- Crash safe: if Portly dies, Windows closes all services (job object).
- Port conflicts are checked before every start: see which program holds the port, switch to a free port in one click, or close that program. Leftover Portly services are cleaned up automatically.
- Modern sidebar UI with light / dark theme that follows Windows.
- **Two editions**: Windows 10/11 (updates, latest packages) and Legacy for Windows 7 SP1 / 8 / 8.1 (frozen, compatible packages).
- 31 languages (right-to-left for Arabic, Persian, Urdu), plain JSON files, contributions welcome.

**Quick start:** download a release, unzip to a folder without spaces (e.g. `C:\Portly`), run `Portly.exe`, install the recommended packages, click **Start all**, create `www\shop` and open `https://shop.portly`.

### Build from source

Requirements: any .NET SDK 6 or newer (the app itself targets .NET Framework 4.8, so it runs on Windows 7 SP1 – 11 without extra runtimes).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

See [docs/02-mimari.md](docs/02-mimari.md) for the architecture and [docs/03-yayinlama.md](docs/03-yayinlama.md) for releasing.

### Translations

Language files live in [`src/Portly/lang`](src/Portly/lang). Copy `en.json`, translate the values, keep `{0}` placeholders and send a pull request. Missing keys fall back to English. See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## Donate

Portly is free and will stay free: no ads, no paid tiers, no tracking. If it saves you time, you can support the development.
Portly ücretsizdir ve öyle kalacak. İşinizi kolaylaştırdıysa geliştirmeye destek olabilirsiniz.

<!-- Bağış bağlantılarınızı buraya ekleyin / add your donation links here (GitHub Sponsors, Buy Me a Coffee, IBAN ...) -->
- GitHub Sponsors: *coming soon*

---

<sub>Apache, PHP, MySQL, Nginx, Node.js, Redis, Mailpit, Composer, Adminer and phpMyAdmin are downloaded from their official sources and are the property of their respective owners. Portly is not affiliated with Laragon.</sub>
