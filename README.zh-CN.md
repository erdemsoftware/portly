<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  适用于 Windows 的快速、便携、开源的本地 Web 开发环境<br>
  <sub>作者 <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · 无广告、无追踪</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <b>简体中文</b> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>应用本身支持 31 种语言(阿拉伯语、波斯语和乌尔都语为从右到左书写)。</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="服务"> <img src="assets/screenshots/en-projects.png" width="49%" alt="项目"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="通过二维码在手机上打开项目"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="新建项目"></p>

Portly 可以从单个文件夹运行 Apache 或 Nginx、PHP、MySQL 或 MariaDB、PostgreSQL、MongoDB、Redis、Memcached、Node.js、Mailpit、Composer、Adminer 和 phpMyAdmin。无需安装程序，默认也不会向 Windows 安装任何内容：没有 Windows 服务(可选的服务模式默认关闭，只有在“设置 > 功能”中开启后才能将环境作为服务运行)，也没有注册表残留。甚至可以从 U 盘运行。

## 为什么选择 Portly？

| | Laragon | **Portly** |
|---|---|---|
| 许可证 | 闭源，商业用途需付费 | **MIT，完全免费，无广告** |
| 应用更新 | 下载新的安装程序 | **应用内一键更新**(经 SHA-256 验证) |
| 软件包列表 | 本地 `packages.conf` | **远程更新的目录** + 来自 windows.php.net 的实时 PHP 版本 |
| 每个站点的 PHP 版本 | 不支持 | **支持**(每个 PHP 版本使用独立的 FastCGI 端口) |
| HTTPS | 一个证书，每个项目重新生成 | **本地根 CA** 只需信任一次，每个站点一个证书 |
| 网络安全 | 监听所有接口 | **默认 127.0.0.1**(不会弹出防火墙提示) |
| PHP 加固 | 默认设置 | `session.upload_progress` 和 `allow_url_include` **关闭** |
| 崩溃安全 | — | 如果 Portly 退出，Windows 会停止它启动的服务器(作业对象) |
| 端口冲突 | 错误信息 | 显示占用端口的程序；**一键切换到空闲端口**或关闭该程序 |
| 共享 | ngrok(需要账户) | **本地网络**(同一 Wi-Fi 下的手机/平板、二维码、https、密码)以及通过 Cloudflare 的**互联网**访问：无需账户的随机地址，或**你自己域名上的固定地址** |
| 迁移 | — | 可迁移来自 **Laragon、XAMPP、WampServer、AMPPS、UwAmp、Uniform Server、Open Server、EasyPHP、MAMP** 的项目和数据库(以及来自 Herd 和 Local 的项目) |
| 数据库 | MySQL | **MySQL 或 MariaDB、PostgreSQL、MongoDB、SQLite**；Redis 和 Memcached |
| 数据库工具 | HeidiSQL | **phpMyAdmin**(root 无密码时自动登录打开)、**Adminer、HeidiSQL、DBeaver、Beekeeper Studio、DbGate、DB Browser for SQLite** |
| WordPress | 手动 | **一键安装 WordPress**(WP-CLI，管理员用户已就绪) |
| 语言 | 34 种(txt) | **31 种**，纯 JSON，欢迎贡献 |
| Windows 7 / 8 / 8.1 | 不明确 | **单独的 Legacy 版** |

## 功能

- **项目**：`www` 中的每个文件夹都会变成 `https://name.portly`；会检测 Laravel 风格的 `public` 文件夹。可以分组文件夹和子项目(`blog.shop.portly`)，提供 Laravel、Symfony、CodeIgniter、CakePHP、Drupal、Vue、React、Svelte、Nuxt、Next.js、Angular 和 Astro 的模板。每个卡片显示项目所用的技术及其数据库。
- **共享项目**
  - *本地网络*：用二维码在手机上打开。通过 https 工作；手机上的指南可一次性在 iPhone 和 Android 上安装 Portly 的证书。可选密码、时间限制、随 Portly 启动、已连接设备列表；来自手机的 PHP 错误会显示在 Portly 的日志中。
  - *互联网*：免费的 Cloudflare 快速隧道(无需账户)，或通过你的 Cloudflare 账户使用你自己的域名，例如 `demo.example.com`。同样支持密码、时间限制和设备列表。
- **每个站点的 PHP 版本**、一键安装 **PHP 扩展**(17 个 PECL 扩展，按 PHP 版本验证)、**Xdebug**，以及跟随项目 PHP / Node 版本的终端(`.php-version`、`composer.json`、`.nvmrc`)。
- **服务**：全部启动 / 全部停止，每个服务都有一个锁图标(可选择哪些服务属于该组)，MySQL / MariaDB 合并在一张卡片中，可选 Tomcat + Java。
- **更新提示**(“PHP 8.6 已发布” → 一键安装并切换)，生命周期结束(EOL)和 CVE 警告。
- **数据库安全**：检查与修复、在 MySQL / MariaDB 版本之间安全迁移、大型 SQL 导入、自动备份、忘记 root 密码时重置、InnoDB 恢复模式。
- **开发者工作流**：后台任务(队列、调度器、Vite)、站点日志、性能分析器、带钩子的 `portly` CLI、`portly.yml` 项目文件、通过 SSH 推送/拉取。
- **站点**：额外域名和通配符域名、自有证书、归档、修复 SSL、Firefox / Java 证书信任，以及 **Caddy** 和 **FrankenPHP**(Octane)。
- **问题预防**：OneDrive 文件夹、非 ASCII 和过长路径、第二个 PHP 或 PHPRC、杀毒软件阻止 hosts 文件、隔离检查。
- 现代侧边栏界面，浅色/深色主题跟随 Windows，支持每显示器 DPI 和屏幕阅读器。很少使用的功能需在“设置 > 功能”中开启。

## 版本

| 下载 | Windows | 更新 | 软件包 |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11(64 位) | **支持**：应用 + 软件包目录 | 最新版：PHP 8.5、MySQL 8.4 LTS / 26.x、Apache 2.4、Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip`(Legacy) | 7 SP1 / 8 / 8.1(64 位) | **不支持**(目录已冻结) | 可在 Windows 7 上运行的最后版本：PHP 8.2、MySQL 5.7、Apache 2.4 VS16、Node 13 |
| macOS / Linux | — | — | **测试版**：见下文 [Portly 2 测试版](#portly-2-beta)。 |

Legacy 版需要 .NET Framework 4.8(已包含在 Windows 10 / 11 中)。

<a id="portly-2-beta"></a>

## Portly 2 测试版：macOS、Linux 与全新的 Windows 应用

Portly 2 可在 **macOS（Apple Silicon 与 Intel）、Linux（x64 与 arm64）以及 Windows 10 / 11** 上运行，所有平台使用同一版本号。目前处于测试阶段：Portly 1.x 仍是稳定版，不会自动升级到 2。

- **macOS / Linux**，一行命令，无需 sudo（校验 SHA-256；macOS：`/Applications/Portly.app`，Linux：`~/.local/share/portly`）：

  ```sh
  curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
  ```

  或从 [Releases](https://github.com/erdemsoftware/portly/releases) 下载 `.dmg`、`.AppImage`、`.deb` 或 `.tar.gz`。
- **Windows 10 / 11**：同一页面中的 `Portly-2.x.y-beta.n-windows-x64.zip`，解压到一个新文件夹中。
- 在 macOS / Linux 上：带 Xdebug 的 PHP、Caddy、Apache、Nginx、MySQL、MariaDB、PostgreSQL、MongoDB、Redis、Memcached、Mailpit 和 Node.js 安装在 `~/Portly` 中；项目在 `https://name.localhost` 打开。

## 快速开始

1. 从 [Releases](https://github.com/erdemsoftware/portly/releases) 下载 zip 文件，并解压到一个**不含空格**的文件夹中，例如 `C:\Portly`。
2. 运行 `Portly.exe`。首次启动时会预选推荐的软件包：点击 **安装所选**。
3. 点击 **全部启动**。
4. 在 `www` 中创建一个文件夹：`www\shop` 会变成 `https://shop.portly`。像域名一样命名的文件夹(`www\example.com`)会以 `https://example.com` 打开，因此上线后地址保持不变；其卡片上的“本地 / 线上”切换开关可以恢复电脑上的线上站点，而本地副本仍可通过 `https://example.com.portly` 访问。

`http://localhost` 会打开一个仪表板，包含所有站点、Adminer、Mailpit 和 `phpinfo()`。更多内容见 [docs/04-kullanim.md](docs/04-kullanim.md)(土耳其语)。

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

要修改配置模板，请从 `templates\` 复制到 `usr\templates\` 并编辑副本；更新不会覆盖它。

## 从源码构建

要求：任意 .NET SDK 6 或更高版本(应用以 .NET Framework 4.8 为目标，因此无需额外运行时即可在 Windows 7 SP1 – 11 上运行)。若安装了 Inno Setup，`build.ps1` 还会生成安装程序。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

架构：[docs/02-mimari.md](docs/02-mimari.md)，发布：[docs/03-yayinlama.md](docs/03-yayinlama.md)(土耳其语)。

## 翻译

语言文件位于 [`src/Portly/lang`](src/Portly/lang)。复制 `en.json`，翻译其中的值，保留 `{0}` 占位符，然后提交 Pull Request。缺失的键会回退为英文。详见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 隐私

Portly 没有遥测、没有分析统计，也没有账户。它只会在你启动或开启相关功能时连接其他系统：

- **更新检查**(默认开启，可在设置中关闭)：从本 GitHub 仓库下载软件包目录和发布信息。不会发送任何关于你或你的项目的信息。
- **安装软件包**：从其官方来源下载(windows.php.net、dev.mysql.com、nodejs.org ...)。
- **在互联网上共享项目**：运行 Cloudflare 的 `cloudflared`；访问者通过 Cloudflare 访问你的站点。本地网络共享则只在你的网络内进行。

只有经过提示，Portly 才会更改系统设置：修改 hosts 文件以及信任其本地证书颁发机构都需要确认(Windows UAC)。

**卸载**：便携版 zip 通过删除其文件夹即可移除；安装版可在 Windows 设置 > 应用中卸载。要同时移除受信任的 Portly 证书颁发机构，请打开 `certmgr.msc` > 受信任的根证书颁发机构 > 证书，并删除 “Portly Local CA”。

## 代码签名政策

Windows 版本由 GitHub Actions 从本仓库构建。已申请通过 SignPath Foundation 进行代码签名；在此之前版本未签名，每个版本都会发布其 SHA-256 校验和。

团队角色：
- 作者 / 提交者：[Erdem Software](https://github.com/erdemsoftware)
- 审阅者：[Erdem Software](https://github.com/erdemsoftware)
- 批准者(发布签名)：[Erdem Software](https://github.com/erdemsoftware)

## 捐助

Portly 免费，并将始终保持免费：无广告、无付费版本、无追踪。如果它为你节省了时间，你可以支持其开发。

- 银行卡：**[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID：**50018297**
- 加密货币(USDT，支持 TRC-20、BEP-20、Polygon、Solana、Ethereum)：在应用中，菜单 > 捐助

---

<sub>Apache、PHP、MySQL、MariaDB、PostgreSQL、MongoDB、Nginx、Node.js、Redis、Memcached、Mailpit、Composer、Adminer、phpMyAdmin、Cloudflare 及其他工具均从其官方来源下载，归其各自所有者所有。Portly 与 Laragon 没有关联。</sub>
