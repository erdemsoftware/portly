<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Lingkungan pengembangan web lokal untuk Windows yang cepat, portabel, dan open-source<br>
  <sub>oleh <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · tanpa iklan, tanpa pelacakan</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <b>Bahasa Indonesia</b></p>
<p align="center"><sub>Aplikasinya sendiri tersedia dalam 31 bahasa (Arab, Persia, dan Urdu ditulis dari kanan ke kiri).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="Layanan"> <img src="assets/screenshots/en-projects.png" width="49%" alt="Proyek"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="Buka proyek di ponsel dengan kode QR"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="Proyek baru"></p>

Portly menjalankan Apache atau Nginx, PHP, MySQL atau MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer, dan phpMyAdmin dari satu folder. Tidak perlu penginstal dan secara default tidak ada yang dipasang ke Windows: tidak ada layanan Windows (mode layanan opsional, nonaktif kecuali Anda mengaktifkannya di Settings > Features, dapat menjalankan tumpukan sebagai layanan), tidak ada sampah registri. Bahkan dapat dijalankan dari flashdisk USB.

## Mengapa Portly?

| | Laragon | **Portly** |
|---|---|---|
| Lisensi | Kode tertutup, berbayar untuk penggunaan komersial | **MIT, sepenuhnya gratis, tanpa iklan** |
| Pembaruan aplikasi | Unduh penginstal baru | **Satu klik di dalam aplikasi** (terverifikasi SHA-256) |
| Daftar paket | `packages.conf` lokal | **Katalog yang diperbarui dari jarak jauh** + versi PHP langsung dari windows.php.net |
| Versi PHP per situs | Tidak | **Ya** (setiap versi PHP pada port FastCGI-nya sendiri) |
| HTTPS | Satu sertifikat, dibuat ulang per proyek | **Root CA lokal** yang dipercaya sekali, sertifikat per situs |
| Keamanan jaringan | Mendengarkan di semua antarmuka | **127.0.0.1 secara default** (tanpa pertanyaan dari firewall) |
| Pengerasan PHP | Default | `session.upload_progress` dan `allow_url_include` **nonaktif** |
| Keamanan saat crash | — | Jika Portly berhenti, Windows menghentikan server yang dijalankannya (job object) |
| Konflik port | Pesan kesalahan | Menampilkan program yang memakai port; **pindah ke port kosong dengan satu klik** atau tutup program tersebut |
| Berbagi | ngrok (perlu akun) | **Jaringan lokal** (ponsel / tablet di Wi-Fi yang sama, kode QR, https, kata sandi) dan **internet** melalui Cloudflare: alamat acak tanpa akun, atau **alamat tetap di domain Anda sendiri** |
| Migrasi | — | Proyek dan database dari **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP** (proyek dari Herd dan Local) |
| Database | MySQL | **MySQL atau MariaDB, PostgreSQL, MongoDB, SQLite**; Redis dan Memcached |
| Alat database | HeidiSQL | **phpMyAdmin** (terbuka dalam keadaan sudah login selama root tidak memiliki kata sandi), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Manual | **WordPress satu klik** (WP-CLI, pengguna admin siap) |
| Bahasa | 34 (txt) | **31**, JSON biasa, terbuka untuk kontribusi |
| Windows 7 / 8 / 8.1 | Tidak jelas | **Edisi Legacy terpisah** |

## Fitur

- **Proyek**: setiap folder di `www` menjadi `https://name.portly`; folder `public` bergaya Laravel dikenali otomatis. Kelompokkan folder dan sub-proyek (`blog.shop.portly`), templat untuk Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular, dan Astro. Setiap kartu menampilkan teknologi yang digunakan proyek dan database-nya.
- **Bagikan proyek**
  - *Jaringan lokal*: buka di ponsel Anda dengan kode QR. Berfungsi melalui https; panduan di ponsel memasang sertifikat Portly di iPhone dan Android sekali saja. Kata sandi opsional, batas waktu, mulai bersama Portly, daftar perangkat yang terhubung; kesalahan PHP dari ponsel muncul di log Portly.
  - *Internet*: terowongan cepat Cloudflare gratis (tanpa akun), atau domain Anda sendiri seperti `demo.example.com` melalui akun Cloudflare Anda. Kata sandi, batas waktu, dan daftar perangkat sama.
- **Versi PHP per situs**, **ekstensi PHP** satu klik (17 ekstensi PECL, diverifikasi per versi PHP), **Xdebug**, terminal yang mengikuti versi PHP / Node proyek (`.php-version`, `composer.json`, `.nvmrc`).
- **Layanan**: Start all / Stop all dengan gembok di setiap layanan (pilih layanan mana yang termasuk grup), MySQL / MariaDB dalam satu kartu, Tomcat + Java opsional.
- **Pemberitahuan pembaruan** ("PHP 8.6 sudah rilis" → pasang dan pindah dalam satu klik), peringatan akhir masa dukungan dan CVE.
- **Keamanan database**: periksa & perbaiki, pemindahan aman antar versi MySQL / MariaDB, impor SQL berukuran besar, cadangan otomatis, reset kata sandi root yang terlupa, mode pemulihan InnoDB.
- **Alur kerja pengembang**: tugas latar (queue, scheduler, Vite), log situs, profiler, CLI `portly` dengan hook, file proyek `portly.yml`, push / pull melalui SSH.
- **Situs**: domain tambahan dan wildcard, sertifikat sendiri, arsip, Fix SSL, kepercayaan sertifikat Firefox / Java, **Caddy** dan **FrankenPHP** (Octane).
- **Pencegahan masalah**: folder OneDrive, karakter non-ASCII dan path panjang, PHP kedua atau PHPRC, antivirus yang memblokir file hosts, pemeriksaan karantina.
- Antarmuka sidebar modern dengan tema terang / gelap yang mengikuti Windows, DPI per monitor, dan dukungan pembaca layar. Fitur yang jarang dipakai diaktifkan di Settings > Features.

## Edisi

| Unduhan | Windows | Pembaruan | Paket |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64-bit) | **Ya**: aplikasi + katalog paket | Terbaru: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64-bit) | **Tidak** (katalog dibekukan) | Versi terakhir yang berjalan di Windows 7: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Segera hadir.** Ekspor pengaturan sudah netral terhadap platform. Beri bintang pada repo atau donasi untuk membantu mewujudkannya. |

Edisi Legacy memerlukan .NET Framework 4.8 (sudah menjadi bagian dari Windows 10 / 11).

## Mulai cepat

1. Unduh zip dari [Releases](https://github.com/erdemsoftware/portly/releases) dan ekstrak ke folder **tanpa spasi**, mis. `C:\Portly`.
2. Jalankan `Portly.exe`. Paket yang direkomendasikan sudah dipilih saat pertama kali dijalankan: klik **Install selected**.
3. Klik **Start all**.
4. Buat folder di `www`: `www\shop` menjadi `https://shop.portly`. Folder yang bernama seperti domain (`www\example.com`) dibuka sebagai `https://example.com`, sehingga URL tetap sama saat Anda online; sakelar Local / Live pada kartunya mengembalikan situs live di PC Anda, sedangkan salinan lokal tetap berfungsi di `https://example.com.portly`.

`http://localhost` membuka dasbor berisi semua situs, Adminer, Mailpit, dan `phpinfo()`. Selengkapnya di [docs/04-kullanim.md](docs/04-kullanim.md) (Bahasa Turki).

```
Portly\
├─ Portly.exe            the app
├─ lang\  templates\     languages and config templates (renewed by updates)
├─ bin\                  packages: bin\php\php-8.5.12-..., bin\mysql\mysql-8.4.11-winx64 ...
├─ etc\                  generated configuration (rewritten at every start)
├─ usr\                  your settings, own templates, SSL authority (never touched by updates)
├─ data\                 databases (one folder per MySQL series)
├─ www\                  your projects (another folder can be chosen in Settings)
└─ logs\  tmp\  backup\
```

Untuk mengubah templat konfigurasi, salin dari `templates\` ke `usr\templates\` dan edit salinannya; pembaruan tidak akan menyentuhnya.

## Build dari sumber

Persyaratan: .NET SDK 6 atau lebih baru (aplikasi menargetkan .NET Framework 4.8, sehingga berjalan di Windows 7 SP1 – 11 tanpa runtime tambahan). Jika Inno Setup terpasang, `build.ps1` juga membuat program setup.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

Arsitektur: [docs/02-mimari.md](docs/02-mimari.md), rilis: [docs/03-yayinlama.md](docs/03-yayinlama.md) (Bahasa Turki).

## Terjemahan

File bahasa berada di [`src/Portly/lang`](src/Portly/lang). Salin `en.json`, terjemahkan nilainya, pertahankan placeholder `{0}`, lalu kirim pull request. Kunci yang belum diterjemahkan akan kembali ke bahasa Inggris. Lihat [CONTRIBUTING.md](CONTRIBUTING.md).

## Privasi

Portly tidak memiliki telemetri, analitik, maupun akun. Portly hanya terhubung ke sistem lain untuk hal-hal yang Anda mulai atau aktifkan:

- **Pemeriksaan pembaruan** (aktif secara default, dapat dimatikan di Settings): mengunduh katalog paket dan informasi rilis dari repo GitHub ini. Tidak ada data tentang Anda atau proyek Anda yang dikirim.
- **Memasang paket**: mengunduh dari sumber resminya (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Berbagi proyek di internet**: menjalankan `cloudflared` milik Cloudflare; pengunjung mencapai situs Anda melalui Cloudflare. Berbagi jaringan lokal tetap berada di dalam jaringan Anda.

Portly hanya mengubah pengaturan sistem dengan konfirmasi: file hosts dan kepercayaan terhadap otoritas sertifikat lokalnya meminta persetujuan (Windows UAC).

**Pencopotan**: versi zip portabel dihapus dengan menghapus foldernya; versi setup dicopot dari Windows Settings > Apps. Untuk menghapus juga otoritas sertifikat Portly yang tepercaya, buka `certmgr.msc` > Trusted Root Certification Authorities > Certificates dan hapus "Portly Local CA".

## Kebijakan penandatanganan kode

Rilis Windows dibangun dari repo ini oleh GitHub Actions. Penandatanganan kode melalui SignPath Foundation sedang diajukan; hingga saat itu rilis belum ditandatangani dan jumlah SHA-256-nya dipublikasikan bersama setiap rilis.

Peran tim:
- Penulis / committer: [Erdem Software](https://github.com/erdemsoftware)
- Peninjau: [Erdem Software](https://github.com/erdemsoftware)
- Penyetuju (penandatanganan rilis): [Erdem Software](https://github.com/erdemsoftware)

## Donasi

Portly gratis dan akan tetap gratis: tanpa iklan, tanpa paket berbayar, tanpa pelacakan. Jika Portly menghemat waktu Anda, Anda dapat mendukung pengembangannya.

- Kartu: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID: **50018297**
- Kripto (USDT di TRC-20, BEP-20, Polygon, Solana, Ethereum): di dalam aplikasi, Menu > Donate

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare dan alat-alat lainnya diunduh dari sumber resminya dan merupakan milik pemiliknya masing-masing. Portly tidak berafiliasi dengan Laragon.</sub>
