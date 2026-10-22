# Yeni sürüm yayınlama ve paket kataloğunu güncelleme

Bu belge Portly'yi yayınlayan kişi içindir.

## Uzaktan güncelleme nasıl işler

Modern sürüm iki dosyayı GitHub'ın `main` dalından okur:

| Dosya | Ne zaman okunur | Ne işe yarar |
|---|---|---|
| `manifest/app.json` | 12 saatte bir ve "Güncellemeleri denetle" ile | Yeni Portly sürümü, zip adresi, SHA-256, değişiklik notu, bağış adresi |
| `manifest/packages.json` | Aynı anda | Paket kataloğu (Apache, MySQL, Node.js ...) |

Ayrıca PHP sürümleri doğrudan `windows.php.net/downloads/releases/releases.json` dosyasından okunur. Yeni bir PHP yaması ya da yeni bir PHP serisi (ör. 8.6) çıkınca katalog değiştirmeden kullanıcılara görünür.

`app.json` içindeki `donateUrl` doldurulursa uygulamadaki tüm "Bağış yap" bağlantıları yeni sürüm çıkarmadan o adrese gider.

## Paket kataloğunu güncellemek (uygulama sürümü gerekmez)

1. `manifest/packages.json` içindeki girdiyi değiştirin ya da yeni girdi ekleyin.
2. Bağlantının çalıştığını deneyin: `curl -I -L <url>`.
3. `main` dalına gönderin. Kullanıcılar en geç 12 saat içinde görür.

Alanlar: `type`, `version`, `folder` (bin altındaki klasör adı), `url`, isteğe bağlı `sha256`, `subdir` (zip içindeki asıl klasör, ör. Apache için `Apache24`), `vc` (gereken VC++ 14.x alt sürümü), `tag` (LTS, newest ...), `note`, `recommended`, `minOs` (10.0 = Win10).

`packages-legacy.json` sabittir. Yalnızca bir bağlantı kırılırsa değiştirilir ve yeni Legacy derlemesiyle dağıtılır.

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
