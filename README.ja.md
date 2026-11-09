<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Windows 向けの、高速でポータブルなオープンソースのローカル Web 開発環境<br>
  <sub>作者 <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · 広告なし、トラッキングなし</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <b>日本語</b> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>アプリ自体は 31 言語に対応しています（アラビア語、ペルシア語、ウルドゥー語は右から左に表示）。</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="サービス"> <img src="assets/screenshots/en-projects.png" width="49%" alt="プロジェクト"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="QR コードでスマートフォンからプロジェクトを開く"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="新規プロジェクト"></p>

Portly は、単一のフォルダーから Apache または Nginx、PHP、MySQL または MariaDB、PostgreSQL、MongoDB、Redis、Memcached、Node.js、Mailpit、Composer、Adminer、phpMyAdmin を実行します。インストーラーは不要で、既定では Windows に何もインストールされません。Windows サービスは登録されず（任意のサービスモードは、設定 > 機能でオンにした場合のみ、スタックをサービスとして実行できます）、レジストリも散らかりません。USB メモリからでも動作します。

## なぜ Portly なのか？

| | Laragon | **Portly** |
|---|---|---|
| ライセンス | クローズドソース、商用利用は有料 | **MIT、完全無料、広告なし** |
| アプリの更新 | 新しいインストーラーをダウンロード | **アプリ内でワンクリック**（SHA-256 検証済み） |
| パッケージ一覧 | ローカルの `packages.conf` | **リモートで更新されるカタログ** + windows.php.net からのライブ PHP バージョン |
| サイトごとの PHP バージョン | いいえ | **はい**（各 PHP バージョンが独自の FastCGI ポートで動作） |
| HTTPS | 証明書は1つ、プロジェクトごとに再作成 | **ローカルのルート CA** を一度信頼させ、サイトごとに証明書 |
| ネットワークの安全性 | すべてのインターフェースで待ち受け | **既定で 127.0.0.1**（ファイアウォールの確認なし） |
| PHP の強化 | 既定値 | `session.upload_progress` と `allow_url_include` は**オフ** |
| クラッシュ時の安全性 | — | Portly が終了すると、Windows が起動したサーバーを停止（ジョブオブジェクト） |
| ポートの競合 | エラーメッセージ | ポートを使用しているプログラムを表示。**ワンクリックで空きポートに切り替え**、またはそのプログラムを終了 |
| 共有 | ngrok（アカウントが必要） | **ローカルネットワーク**（同じ Wi-Fi 上のスマートフォン / タブレット、QR コード、https、パスワード）と、Cloudflare 経由の**インターネット**：アカウント不要のランダムなアドレス、または**自分のドメインの固定アドレス** |
| 移行 | — | **Laragon、XAMPP、WampServer、AMPPS、UwAmp、Uniform Server、Open Server、EasyPHP、MAMP** からのプロジェクトとデータベース（Herd および Local からのプロジェクト） |
| データベース | MySQL | **MySQL または MariaDB、PostgreSQL、MongoDB、SQLite**、Redis と Memcached |
| データベースツール | HeidiSQL | **phpMyAdmin**（root パスワードが未設定の間はログイン済みで開く）、**Adminer、HeidiSQL、DBeaver、Beekeeper Studio、DbGate、DB Browser for SQLite** |
| WordPress | 手動 | **ワンクリック WordPress**（WP-CLI、管理ユーザー作成済み） |
| 言語 | 34 (txt) | **31**、プレーン JSON、貢献を歓迎 |
| Windows 7 / 8 / 8.1 | 不明 | **別の Legacy エディション** |

## 機能

- **プロジェクト**: `www` 内の各フォルダーは `https://name.portly` になります。Laravel 形式の `public` フォルダーは自動検出されます。フォルダーのグループ化とサブプロジェクト（`blog.shop.portly`）、Laravel、Symfony、CodeIgniter、CakePHP、Drupal、Vue、React、Svelte、Nuxt、Next.js、Angular、Astro のテンプレート。各カードには、プロジェクトの構成技術とデータベースが表示されます。
- **プロジェクトの共有**
  - *ローカルネットワーク*: QR コードでスマートフォンから開きます。https で動作します。スマートフォン上のガイドに従い、iPhone と Android に Portly の証明書を一度インストールします。任意のパスワード、時間制限、Portly と同時に起動、接続中のデバイス一覧。スマートフォンからの PHP エラーは Portly のログに表示されます。
  - *インターネット*: 無料の Cloudflare クイックトンネル（アカウント不要）、または Cloudflare アカウント経由で `demo.example.com` のような独自ドメイン。パスワード、時間制限、デバイス一覧は同じです。
- **サイトごとの PHP バージョン**、ワンクリックの **PHP 拡張**（17 の PECL 拡張、PHP バージョンごとに検証済み）、**Xdebug**、プロジェクトの PHP / Node バージョンに従うターミナル（`.php-version`、`composer.json`、`.nvmrc`）。
- **サービス**: 各サービスに錠前アイコンが付いた「すべて開始 / すべて停止」（どのサービスをグループに含めるか選択可能）、MySQL / MariaDB は1つのカードに、任意で Tomcat + Java。
- **更新通知**（「PHP 8.6 が公開されました」→ ワンクリックでインストールして切り替え）、サポート終了および CVE 警告。
- **データベースの安全性**: 検査と修復、MySQL / MariaDB バージョン間の安全な移行、大容量 SQL インポート、自動バックアップ、忘れた root パスワードのリセット、InnoDB 復旧モード。
- **開発者ワークフロー**: バックグラウンドジョブ（キュー、スケジューラー、Vite）、サイトログ、プロファイラー、フック付き `portly` CLI、`portly.yml` プロジェクトファイル、SSH 経由の push / pull。
- **サイト**: 追加ドメインとワイルドカードドメイン、独自証明書、アーカイブ、SSL 修復、Firefox / Java の証明書信頼、**Caddy** と **FrankenPHP**（Octane）。
- **問題の予防**: OneDrive フォルダー、ASCII 以外の文字や長いパス、2つ目の PHP や PHPRC、hosts ファイルをブロックするウイルス対策ソフト、隔離チェック。
- Windows に追従するライト / ダークテーマを備えた、モダンなサイドバー UI。モニターごとの DPI とスクリーンリーダー対応。あまり使われない機能は設定 > 機能でオンにします。

## エディション

| ダウンロード | Windows | 更新 | パッケージ |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11（64 ビット） | **はい**: アプリ + パッケージカタログ | 最新: PHP 8.5、MySQL 8.4 LTS / 26.x、Apache 2.4、Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip`（Legacy） | 7 SP1 / 8 / 8.1（64 ビット） | **いいえ**（凍結されたカタログ） | Windows 7 で動作する最後のバージョン: PHP 8.2、MySQL 5.7、Apache 2.4 VS16、Node 13 |
| macOS / Linux | — | — | **近日公開。** 設定のエクスポートはすでにプラットフォーム非依存です。実現に向けて、リポジトリにスターを付けるか寄付してください。 |

Legacy エディションには .NET Framework 4.8 が必要です（Windows 10 / 11 には標準で含まれています）。

## クイックスタート

1. [Releases](https://github.com/erdemsoftware/portly/releases) から zip をダウンロードし、**スペースのないフォルダー**（例: `C:\Portly`）に展開します。
2. `Portly.exe` を実行します。初回起動時に推奨パッケージが選択済みになっています。**選択したものをインストール** をクリックします。
3. **すべて開始** をクリックします。
4. `www` 内にフォルダーを作成します: `www\shop` は `https://shop.portly` になります。ドメイン名の形式のフォルダー（`www\example.com`）は `https://example.com` として開かれるため、公開時も URL は変わりません。カードの Local / Live 切り替えで、PC 上で公開サイトを表示できます。ローカルコピーは `https://example.com.portly` で引き続き動作します。

`http://localhost` ではすべてのサイト、Adminer、Mailpit、`phpinfo()` を備えたダッシュボードが開きます。詳細は [docs/04-kullanim.md](docs/04-kullanim.md)（トルコ語）を参照してください。

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

設定テンプレートを変更するには、`templates\` から `usr\templates\` にコピーし、そのコピーを編集します。更新ではそのまま残ります。

## ソースからのビルド

必要なもの: .NET SDK 6 以降（アプリは .NET Framework 4.8 を対象とするため、追加ランタイムなしで Windows 7 SP1 〜 11 で動作します）。Inno Setup がインストールされていれば、`build.ps1` はセットアッププログラムも作成します。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

アーキテクチャ: [docs/02-mimari.md](docs/02-mimari.md)、リリース: [docs/03-yayinlama.md](docs/03-yayinlama.md)（トルコ語）。

## 翻訳

言語ファイルは [`src/Portly/lang`](src/Portly/lang) にあります。`en.json` をコピーし、値を翻訳して、`{0}` のプレースホルダーはそのまま残し、プルリクエストを送ってください。不足しているキーは英語にフォールバックします。詳しくは [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。

## プライバシー

Portly にはテレメトリー、アナリティクス、アカウントはありません。ユーザーが開始またはオンにした機能のためにのみ、他のシステムに接続します:

- **更新の確認**（既定でオン、設定でオフにできます）: この GitHub リポジトリからパッケージカタログとリリース情報をダウンロードします。ユーザーやプロジェクトに関する情報は一切送信されません。
- **パッケージのインストール**: 公式ソース（windows.php.net、dev.mysql.com、nodejs.org ...）からダウンロードします。
- **プロジェクトのインターネット共有**: Cloudflare の `cloudflared` を実行します。訪問者は Cloudflare 経由でサイトにアクセスします。ローカルネットワークでの共有はネットワーク内にとどまります。

Portly はシステム設定を、確認を経てのみ変更します。hosts ファイルの変更とローカル認証局の信頼設定は、確認を求めます（Windows UAC）。

**アンインストール**: ポータブル版の zip は、フォルダーを削除すると消えます。セットアップ版は Windows の設定 > アプリからアンインストールします。信頼された Portly 認証局も削除するには、`certmgr.msc` > 信頼されたルート証明機関 > 証明書 を開き、「Portly Local CA」を削除します。

## コード署名ポリシー

Windows 版のリリースは、GitHub Actions によってこのリポジトリからビルドされます。SignPath Foundation によるコード署名を申請中です。それまでリリースは署名されておらず、各リリースとともに SHA-256 チェックサムが公開されます。

チームの役割:
- 作成者 / コミッター: [Erdem Software](https://github.com/erdemsoftware)
- レビュー担当者: [Erdem Software](https://github.com/erdemsoftware)
- 承認者（リリース署名）: [Erdem Software](https://github.com/erdemsoftware)

## 寄付

Portly は無料であり、今後も無料であり続けます。広告、有料プラン、トラッキングはありません。時間の節約に役立ったなら、開発を支援できます。

- カード: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID: **50018297**
- 暗号資産（TRC-20、BEP-20、Polygon、Solana、Ethereum 上の USDT）: アプリ内で メニュー > 寄付

---

<sub>Apache、PHP、MySQL、MariaDB、PostgreSQL、MongoDB、Nginx、Node.js、Redis、Memcached、Mailpit、Composer、Adminer、phpMyAdmin、Cloudflare などのツールは公式ソースからダウンロードされるもので、それぞれの所有者に帰属します。Portly は Laragon と提携していません。</sub>
