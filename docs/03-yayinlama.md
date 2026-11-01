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

## Manifest imzalama

Uygulamanın indirdiği her `manifest/*.json` dosyasının yanında ayrık imzası (`<ad>.json.sig`) durur: tek satır base64, RSA PKCS#1 v1.5 + SHA-256, dosyanın **tam baytları** üzerinde. Windows 7 ve .NET Framework 4.8'de yerleşik olduğu için RSA-3072 kullanılır; Ed25519 yoktur ve NuGet alınmaz.

Uygulama imzası eksik ya da bozuk dosyayı **reddeder**, imzasız yedek yol yoktur. Bir önceki kopya (önbellek ya da exe içine gömülü olan) kullanılmaya devam eder, Paketler penceresinde ve günlükte açık bir ileti çıkar. `usr\cache` içindeki kopyalar imzalarıyla saklanır, her okumada yeniden doğrulanır; imzası tutmayan kopya silinir. Exe içine gömülü kopyalar imzalı sürümün parçası sayılır, ayrıca doğrulanmaz. `app.json` ayrıca çalışan sürümden yeni olmalıdır, eski bir imzalı dosya ile sürüm düşürülemez. Eski Portly sürümleri `.sig` dosyalarını görmezden gelir, onlar için bir şey değişmez.

**Satır sonları:** `.gitattributes` içindeki `manifest/*.json -text` kuralı kaldırılmamalıdır. Windows'ta `core.autocrlf=true` iken git dosyayı CRLF çıkarır, GitHub ise depodaki baytları sunar; kural olmazsa imza, kullanıcının indirdiği dosyayla tutmaz.

### Anahtarı ilk kez oluşturmak

Özel anahtar yalnızca sizde ve GitHub'ın gizli değerlerinde durur, depoya girmez. Depo dışındaki bir klasörde çalıştırın:

```powershell
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
[IO.File]::WriteAllText("$PWD\portly-manifest-private.xml", $rsa.ToXmlString($true))   # GİZLİ
$rsa.ToXmlString($false)                                                                # açık anahtar
```

1. Çıkan açık anahtarı (tek satır, `<RSAKeyValue>` ile başlar) `src/Portly/Packages/ManifestKeys.cs` içindeki satıra koyun: `public const string PublicKey1 = "<RSAKeyValue>...</RSAKeyValue>";`. Başka değişiklik gerekmez.
2. Özel anahtar dosyasının içeriğini GitHub'da **Settings > Secrets and variables > Actions** altına `PORTLY_MANIFEST_KEY` adıyla ekleyin. PEM de kabul edilir (PowerShell 7 gerekir). Dosyayı güvenli bir yerde saklayıp diskten silin.
3. Önerilen: **Settings > Environments > `manifest-signing`** ortamına zorunlu onaylayıcı ekleyin ve gizli değeri bu ortama koyun. Üç iş akışı da bu ortamı kullanır. Onaylayıcı yoksa `main` dalına yazabilen herkes (ya da çalınmış bir erişim) değiştirdiği manifesti `sign-manifests` ile imzalatmış olur; imza o zaman yalnızca barındırma yolunu korur, depoyu değil.

4. **İlk devreye alma sırası:** anahtar kaynağa ve gizli değere girdikten sonra kodu `main` dalına alın, sonra **Actions > sign-manifests > Run workflow** ile elle çalıştırın (birleştirme `manifest/*.json` dosyalarını değiştirmediği için iş kendiliğinden başlamaz). `.sig` dosyaları `main` üzerinde görününce etiketi atın. İlk imzalı sürüme geçiş, eski istemcilerde hâlâ imzasız yoldan olur; korumayı o sürümden sonraki güncellemeler alır.

### Ne zaman imzalanır

- **Yeni sürüm:** `release.yml` en başta anahtarı denetler (`sign-manifests.ps1 -CheckKey`). Gizli değer yoksa, kaynakta hâlâ yer tutucu varsa ya da anahtar `ManifestKeys.cs` ile eşleşmiyorsa sürüm hiçbir şey yayınlanmadan **başarısız olur**. En sonda `app.json` doldurulduktan sonra `main` üzerindeki tüm manifestleri imzalar, işlenen baytları açık anahtarlarla doğrular ve gönderir.
- **`main` üzerinde manifest değişince:** `sign-manifests.yml` imzaları kendisi yeniler. Bot yalnızca `.sig` dosyalarını değiştirir ve `GITHUB_TOKEN` ile gönderir, bu yüzden döngü oluşmaz. İş bitene kadar (bir iki dakika) yeni istemciler yeni dosyayı reddeder ve eskisiyle devam eder.
- **Elle:** `$env:PORTLY_MANIFEST_KEY = Get-Content anahtar.xml -Raw`, sonra `powershell -File tools\sign-manifests.ps1`. Denetim için gizli değer gerekmez: `tools\sign-manifests.ps1 -VerifyCommitted`.

### Anahtar değiştirme

1. Yeni açık anahtarı `PublicKey2` olarak ekleyip `Trusted` dizisine yazın, sürüm çıkarın.
2. Geçiş süresince eski anahtar `PORTLY_MANIFEST_KEY`, yeni anahtar `PORTLY_MANIFEST_KEY_2` olsun: `.sig` dosyasına iki satır yazılır, iki kuşak istemci de doğrular.
3. İstemciler yeni sürüme geçtikten sonra eski anahtarı gizli değerden ve sonraki bir sürümde kaynaktan çıkarın.

Özel anahtar kaybolursa imzalı güncelleme yapılamaz: mevcut istemciler yeni dosyaları reddeder ve kullanıcıların yeni sürümü elle kurması gerekir.

## Paket özetleri (SHA-256)

`Installer` özeti tutmayan paketi siler ve kurmaz. Özeti olmayan paket HTTPS ile kurulmaya devam eder, günlüğe uyarı düşer; HTTP adresli ve özetsiz paket reddedilir.

Eksik özetleri **Actions > fill-package-hashes > Run workflow** doldurur (`tools/fill-package-hashes.ps1`, çok GB indirir, yerelde çalıştırmayın). Bulunan her özet hemen yazılır, iş yeniden başlatılınca kaldığı yerden devam eder; sonunda imzalanıp `main` dalına gönderilir. Özet listesi yalnızca oynak adresler kalana kadar tekrar çalıştırın. Legacy kataloğu exe içindedir, özetler bir sonraki Legacy derlemesiyle gelir.

Oynak adresler (`latest`, `gh-pages`: Composer, VS Code, WP-CLI) bilerek atlanır: dosya her sürümde değişir, sabit özet bir sonraki iyi sürümü de reddederdi. Üretici bir dosyayı aynı adresle yeniden yayınlarsa kurulum "dosya hasarlı" hatası verir; özeti girdiden silip iş akışını yeniden çalıştırın.

## Paket kataloğunu güncellemek (uygulama sürümü gerekmez)

1. `manifest/packages.json` içindeki girdiyi değiştirin ya da yeni girdi ekleyin.
2. Bağlantının çalıştığını deneyin: `curl -I -L <url>`. Sürümü sabit yeni girdilere `sha256` ekleyin (`(Get-FileHash dosya -Algorithm SHA256).Hash.ToLower()`) ya da fill-package-hashes işini çalıştırın.
3. `main` dalına gönderin. `sign-manifests` imzayı yeniler. Kullanıcılar en geç 12 saat içinde görür.

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
Portly-1.0.1-win10-11-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="D:\Portly"
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

`release.yml` önce imza anahtarını denetler ve güvenlik testlerini (`tools/run-security-tests.ps1`: `--share-security`, `--updates-security`) çalıştırır; biri başarısızsa hiçbir şey yayınlanmaz. Sonra iki sürümü derler, zip'leri ve `SHA256SUMS.txt` dosyasını GitHub sürümüne ekler, ardından `manifest/app.json` dosyasını yeni sürüm, adres ve SHA-256 ile günceller, tüm manifestleri imzalayıp `main` dalına gönderir. Kullanıcılar o andan sonra güncellemeyi görür.

Bu son adım sürüm yayınlandıktan sonra çalışır. Başarısız olursa sürüm durur ama kullanıcılara duyurulmaz. İşi yeniden çalıştırmayın (`gh release create` sürüm zaten var diye durur); aşağıdaki "Elle" bölümünün 2. adımını uygulayın.

Değişiklik notunu etiketlemeden önce `manifest/app.json` içindeki `notes` alanına yazın.

### Elle

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.1 -UpdateManifest
```

1. `dist\` içindeki zip'leri GitHub'da `v1.0.1` sürümüne yükleyin.
2. Sürüm yayınlandıktan **sonra** güncellenmiş `manifest/app.json` dosyasını imzalayıp (`tools\sign-manifests.ps1`) `.sig` dosyalarıyla birlikte `main` dalına gönderin. Sıra önemlidir: önce gönderilirse kullanıcılar henüz var olmayan dosyayı indirmeye çalışır.

Inno Setup 6 kuruluysa `build.ps1` kurulum programlarını da üretir.

## Yayın öncesi kontrol listesi

- [ ] `dotnet run --project tests\Portly.SmokeTest -c Release` tüm adımları geçti
- [ ] `PORTLY_MANIFEST_KEY` tanımlı, `ManifestKeys.cs` içinde gerçek açık anahtar var
- [ ] Yayından sonra `main` üzerinde `tools\sign-manifests.ps1 -VerifyCommitted` geçiyor
- [ ] İki sürüm de derleniyor (`build.ps1`)
- [ ] Yeni metinler `en.json` ve `tr.json` dosyalarına eklendi (diğer diller İngilizceye düşer)
- [ ] Üretilen `php.ini` içinde `session.upload_progress.enabled = Off` duruyor
- [ ] `app.json` notları yazıldı
- [ ] `security.json` içindeki EOL tarihleri ve duyurular güncel (kaynaklardan kontrol edildi)
- [ ] İmza sırları varsa `Portly.exe` imzalı çıktı (`Get-AuthenticodeSignature`)
- [ ] winget / Scoop dosyaları (`package-manifests` artifact) gönderildi
