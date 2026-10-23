# Yeni sürüm yayınlama ve paket kataloğunu güncelleme

Bu belge Portly'yi yayınlayan kişi içindir.

## Uzaktan güncelleme nasıl işler

Modern sürüm iki dosyayı GitHub'ın `main` dalından okur:

| Dosya | Ne zaman okunur | Ne işe yarar |
|---|---|---|
| `manifest/app.json` | 12 saatte bir ve "Güncellemeleri denetle" ile | Yeni Portly sürümü, zip adresi, SHA-256, değişiklik notu, bağış adresi |
| `manifest/packages.json` | Günün ilk açılışında ve "Güncellemeleri denetle" ile | Paket kataloğu (Apache, MySQL, Node.js ...). Yeni sürüm bildirimleri ("PHP 8.6 çıktı") buradan hesaplanır |
| `manifest/security.json` | Katalogla birlikte | Destek bitiş (EOL) tarihleri, güvenlik duyuruları (CVE), php.ini sıcak düzeltmeleri |

Ayrıca PHP sürümleri doğrudan `windows.php.net/downloads/releases/releases.json` dosyasından okunur. Yeni bir PHP yaması ya da yeni bir PHP serisi (ör. 8.6) çıkınca katalog değiştirmeden kullanıcılara görünür.

`app.json` içindeki `donateUrl` doldurulursa uygulamadaki tüm "Bağış yap" bağlantıları yeni sürüm çıkarmadan o adrese gider.

## Paket kataloğunu güncellemek (uygulama sürümü gerekmez)

1. `manifest/packages.json` içindeki girdiyi değiştirin ya da yeni girdi ekleyin.
2. Bağlantının çalıştığını deneyin: `curl -I -L <url>`.
3. `main` dalına gönderin. Kullanıcılar en geç 12 saat içinde görür.

Alanlar: `type`, `version`, `folder` (bin altındaki klasör adı), `url`, isteğe bağlı `sha256`, `subdir` (zip içindeki asıl klasör, ör. Apache için `Apache24`), `vc` (gereken VC++ 14.x alt sürümü), `tag` (LTS, newest ...), `note`, `recommended`, `minOs` (10.0 = Win10).

`packages-legacy.json` sabittir. Yalnızca bir bağlantı kırılırsa değiştirilir ve yeni Legacy derlemesiyle dağıtılır.

## Güvenlik dosyası (`manifest/security.json`)

Uygulama sürümü gerekmeden güncellenir; Modern sürüm katalogla birlikte indirir, Legacy yalnızca exe içine gömülü kopyayı kullanır.

- `eol`: `{ "type": "php", "branch": "8.1", "eol": "2025-12-31", "lts": true }`. Dal (branch) Node.js ve PostgreSQL için ana sürüm (`24`, `18`), diğerleri için `ana.alt` (`8.4`, `1.28`, `11.8`). Tarih geçmişse servis kartında turuncu "Desteği bitti" etiketi ve Paketler penceresinde açıklama çıkar. `lts: true` olan dallar MySQL / MariaDB / Node.js / MongoDB için "yeni dal çıktı" bildirimine aday olur (ör. MySQL 8.4 kullanana 9.7 önerilir, 26.x Innovation önerilmez).
- `advisories`: `{ "type", "branch", "fixedIn", "cve", "severity", "url", "note" }`. Kurulu sürüm o dalda ve `fixedIn` sürümünden eskiyse kırmızı "Güvenlik düzeltmesi" etiketi çıkar; tıklanınca aynı dalın düzeltilmiş sürümüne tek tıkla geçilir.
- `iniHotfixes`: tüm php.ini dosyalarına uygulanır, ama **yalnızca** koddaki beyaz listede olan anahtar/değerler (`SecurityInfo.IniWhitelist`: `session.upload_progress.enabled=Off`, `allow_url_include=Off`, `expose_php=Off`, `enable_dl=Off`, `session.use_strict_mode=1` ...). Başka her şey yok sayılır; uzak dosya PHP'yi gevşetemez, kod çalıştıramaz.
- Tarihleri resmî kaynaklardan doğrulayın (dosyadaki `sources` alanı: php.net/supported-versions, endoflife.date, üretici sayfaları). Bozuk JSON gönderilirse uygulama önceki kopyayı kullanmaya devam eder.

## Kod imzalama

İmzasız exe dosyaları SmartScreen ve bazı antivirüslerde uyarı alır. `build.ps1` ortamda `PORTLY_SIGN_PFX` (pfx dosya yolu) ve `PORTLY_SIGN_PASSWORD` varsa `Portly.exe` ile kurulum programlarını `signtool` ile imzalar (zaman damgası: DigiCert). Yoksa imzalamadan devam eder.

GitHub Actions'ta: depo ayarlarında **Secrets** altına `SIGN_PFX_BASE64` (pfx dosyasının base64 hâli: `[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`) ve `SIGN_PFX_PASSWORD` ekleyin. Gizli değerler yoksa imzalama adımı atlanır.

### SignPath Foundation (açık kaynak için ücretsiz sertifika)

1. <https://signpath.org> adresinden "Apply" formunu doldurun: proje adı Portly, depo `https://github.com/erdemsoftware/portly`, lisans MIT. Şartlar: OSI onaylı lisans, herkese açık derleme (GitHub Actions), sürümlerin depodaki koddan otomatik derlenmesi, projede kötü amaçlı/istenmeyen davranış olmaması.
2. Onaydan sonra SignPath'te bir "project" ve "signing policy" (release) açılır; GitHub hesabınız bağlanır.
3. `release.yml` içinde derlenen dosyalar `actions/upload-artifact` ile yüklenir, ardından `signpath/github-action-submit-signing-request` eylemi (`api-token: ${{ secrets.SIGNPATH_API_TOKEN }}`, `organization-id`, `project-slug`, `signing-policy-slug`, `github-artifact-id`) imzalı dosyaları geri indirir. İmzalı `Portly.exe` ile zip ve kurulum yeniden paketlenmeli, `SHA256SUMS.txt` ondan sonra üretilmelidir.
4. SignPath sertifikasıyla imzalanan dosyalarda yayıncı "SignPath Foundation" görünür; README'de "Free code signing provided by SignPath.io, certificate by SignPath Foundation" notu istenir.

Kendi sertifikanız olursa (OV/EV) yukarıdaki `SIGN_PFX_*` yolu yeterlidir.

## winget, Scoop ve sessiz kurulum

Kurulum programı sessiz kurulumu destekler:

```
Portly-1.0.1-win10-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="D:\Portly"
unins000.exe /VERYSILENT /SUPPRESSMSGBOXES            (paketler, veritabanları, projeler, ayarlar kalır)
unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /PURGE     (hepsi silinir)
```

`build.ps1` her derlemede `tools\make-package-manifests.ps1` ile `build\manifests\` altına şablonlardan (`manifests\winget\*.yaml`, `manifests\scoop\portly.json`) sürüm, adres ve SHA-256 doldurulmuş dosyaları üretir. GitHub Actions bunları `package-manifests` adlı artifact olarak saklar.

**winget** (ilk kez ve her sürümde):

1. `microsoft/winget-pkgs` deposunu fork'layın.
2. `build\manifests\winget\manifests\e\ErdemSoftware\Portly\<sürüm>\` klasörünü fork'ta aynı yola kopyalayın.
3. Denetleyin: `winget validate --manifest <klasör>` ve `winget install --manifest <klasör>` (Windows Sandbox'ta önerilir).
4. Pull request açın. Sonraki sürümlerde `wingetcreate update ErdemSoftware.Portly --version <sürüm> --urls <setup url> --submit` de kullanılabilir.

**Scoop**: ayrı bir bucket deposu açın (ör. `erdemsoftware/scoop-bucket`), `build\manifests\scoop\portly.json` dosyasını `bucket\portly.json` olarak gönderin. Kullanıcı: `scoop bucket add erdemsoftware https://github.com/erdemsoftware/scoop-bucket` ve `scoop install portly`. Dosyadaki `checkver`/`autoupdate` sayesinde bucket'ın Excavator işi sonraki sürümleri kendisi günceller. `persist` ile `bin`, `data`, `www`, `usr`, `backup` klasörleri güncellemelerde korunur.

## Yeni Portly sürümü

### GitHub Actions ile (önerilen)

```bash
git tag v1.0.1
git push origin v1.0.1
```

`release.yml` iki sürümü derler, zip'leri ve `SHA256SUMS.txt` dosyasını GitHub sürümüne ekler, ardından `manifest/app.json` dosyasını yeni sürüm, adres ve SHA-256 ile güncelleyip `main` dalına gönderir. Kullanıcılar o andan sonra güncellemeyi görür.

Değişiklik notunu etiketlemeden önce `manifest/app.json` içindeki `notes` alanına yazın.

### Elle

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.1 -UpdateManifest
```

1. `dist\` içindeki zip'leri GitHub'da `v1.0.1` sürümüne yükleyin.
2. Sürüm yayınlandıktan **sonra** güncellenmiş `manifest/app.json` dosyasını `main` dalına gönderin. Sıra önemlidir: önce gönderilirse kullanıcılar henüz var olmayan dosyayı indirmeye çalışır.

Inno Setup 6 kuruluysa `build.ps1` kurulum programlarını da üretir.

## Yayın öncesi kontrol listesi

- [ ] `dotnet run --project tests\Portly.SmokeTest -c Release` tüm adımları geçti
- [ ] İki sürüm de derleniyor (`build.ps1`)
- [ ] Yeni metinler `en.json` ve `tr.json` dosyalarına eklendi (diğer diller İngilizceye düşer)
- [ ] Üretilen `php.ini` içinde `session.upload_progress.enabled = Off` duruyor
- [ ] `app.json` notları yazıldı
- [ ] `security.json` içindeki EOL tarihleri ve duyurular güncel (kaynaklardan kontrol edildi)
- [ ] İmza sırları varsa `Portly.exe` imzalı çıktı (`Get-AuthenticodeSignature`)
- [ ] winget / Scoop dosyaları (`package-manifests` artifact) gönderildi
