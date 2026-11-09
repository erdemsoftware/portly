<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Быстрая, портативная, открытая локальная среда веб-разработки для Windows<br>
  <sub>от <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · без рекламы и отслеживания</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <b>Русский</b> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>Само приложение говорит на 31 языке (арабский, фарси и урду — справа налево).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="Службы"> <img src="assets/screenshots/en-projects.png" width="49%" alt="Проекты"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="Открытие проекта на телефоне по QR-коду"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="Новый проект"></p>

Portly запускает Apache или Nginx, PHP, MySQL или MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer и phpMyAdmin из одной папки. Установка не требуется, и по умолчанию ничего не устанавливается в Windows: никаких служб Windows (необязательный режим службы, отключённый по умолчанию, можно включить в Настройки > Функции, и тогда стек работает как служба), без мусора в реестре. Работает даже с USB-флешки.

## Почему Portly?

| | Laragon | **Portly** |
|---|---|---|
| Лицензия | Закрытый исходный код, платно для коммерческого использования | **MIT, полностью бесплатно, без рекламы** |
| Обновления приложения | Скачать новый установщик | **Одним нажатием внутри приложения** (проверка SHA-256) |
| Список пакетов | Локальный `packages.conf` | **Удалённо обновляемый каталог** + актуальные версии PHP с windows.php.net |
| Версия PHP для каждого сайта | Нет | **Да** (каждая версия PHP на своём порту FastCGI) |
| HTTPS | Один сертификат, пересоздаётся для каждого проекта | **Локальный корневой ЦС**, доверенный один раз, сертификат для каждого сайта |
| Сетевая безопасность | Слушает все интерфейсы | **По умолчанию 127.0.0.1** (без запроса брандмауэра) |
| Защита PHP | Настройки по умолчанию | `session.upload_progress` и `allow_url_include` **выключены** |
| Безопасность при сбоях | — | Если Portly завершится, Windows остановит запущенные им серверы (job object) |
| Конфликты портов | Сообщение об ошибке | Показывает программу, занимающую порт; **переключение на свободный порт в один клик** или закрытие этой программы |
| Публикация | ngrok (нужна учётная запись) | **Локальная сеть** (телефон / планшет в той же Wi-Fi сети, QR-код, https, пароль) и **интернет** через Cloudflare: случайный адрес без учётной записи или **постоянный адрес на вашем домене** |
| Миграция | — | Проекты и базы данных из **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP** (проекты из Herd и Local) |
| Базы данных | MySQL | **MySQL или MariaDB, PostgreSQL, MongoDB, SQLite**; Redis и Memcached |
| Инструменты для БД | HeidiSQL | **phpMyAdmin** (открывается с авторизацией, пока у root нет пароля), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Вручную | **WordPress в один клик** (WP-CLI, администратор уже создан) |
| Языки | 34 (txt) | **31**, простой JSON, открыт для вкладов |
| Windows 7 / 8 / 8.1 | Не ясно | **Отдельная версия Legacy** |

## Возможности

- **Проекты**: каждая папка в `www` становится `https://name.portly`; папки `public` в стиле Laravel распознаются. Группируйте папки и подпроекты (`blog.shop.portly`), шаблоны для Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular и Astro. Каждая карточка показывает, на чём построен проект, и его базу данных.
- **Публикация проекта**
  - *Локальная сеть*: откройте проект на телефоне по QR-коду. Работает по https; инструкция на телефоне один раз устанавливает сертификат Portly на iPhone и Android. Необязательный пароль, ограничение по времени, запуск вместе с Portly, список подключённых устройств; ошибки PHP с телефона выводятся в журнал Portly.
  - *Интернет*: бесплатный быстрый туннель Cloudflare (без учётной записи) или ваш собственный домен, например `demo.example.com`, через вашу учётную запись Cloudflare. Тот же пароль, ограничение по времени и список устройств.
- **Версия PHP для каждого сайта**, **расширения PHP** в один клик (17 расширений PECL, проверены для каждой версии PHP), **Xdebug**, терминалы, которые следуют версии PHP / Node проекта (`.php-version`, `composer.json`, `.nvmrc`).
- **Службы**: Запустить все / Остановить все с замком у каждой службы (выберите, какие входят в группу), MySQL / MariaDB в одной карточке, необязательные Tomcat + Java.
- **Уведомления об обновлениях** («Вышел PHP 8.6» → установка и переключение в один клик), предупреждения о прекращении поддержки и CVE.
- **Безопасность баз данных**: проверка и восстановление, безопасный перенос между версиями MySQL / MariaDB, импорт больших SQL-файлов, автоматические резервные копии, сброс забытого пароля root, режим восстановления InnoDB.
- **Рабочий процесс разработчика**: фоновые задачи (очередь, планировщик, Vite), журналы сайта, профилировщик, CLI `portly` с хуками, файл проекта `portly.yml`, push / pull через SSH.
- **Сайты**: дополнительные и подстановочные домены, собственные сертификаты, архивация, Fix SSL, доверие сертификатам в Firefox / Java, **Caddy** и **FrankenPHP** (Octane).
- **Профилактика проблем**: папки OneDrive, пути с не-ASCII символами и длинные пути, второй PHP или PHPRC, антивирус, блокирующий файл hosts, проверка карантина.
- Современный интерфейс с боковой панелью, светлая / тёмная тема, следующая за Windows, поддержка DPI для каждого монитора и экранных читалок. Редко используемые функции включаются в Настройки > Функции.

## Издания

| Загрузка | Windows | Обновления | Пакеты |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64-бит) | **Да**: приложение + каталог пакетов | Последние: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64-бит) | **Нет** (каталог заморожен) | Последние версии, работающие в Windows 7: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Скоро.** Экспорт настроек уже не зависит от платформы. Поставьте звезду репозиторию или сделайте пожертвование, чтобы это произошло. |

Для версии Legacy нужен .NET Framework 4.8 (уже входит в Windows 10 / 11).

## Быстрый старт

1. Скачайте zip-архив из [Releases](https://github.com/erdemsoftware/portly/releases) и распакуйте его в папку **без пробелов**, например `C:\Portly`.
2. Запустите `Portly.exe`. Рекомендуемые пакеты отмечены при первом запуске: нажмите **Установить выбранные**.
3. Нажмите **Запустить все**.
4. Создайте папку в `www`: `www\shop` становится `https://shop.portly`. Папка, названная как домен (`www\example.com`), открывается как `https://example.com`, поэтому адреса не меняются при публикации; переключатель Local / Live на её карточке возвращает на вашем ПК живой сайт, а локальная копия продолжает работать по адресу `https://example.com.portly`.

`http://localhost` открывает панель со всеми сайтами, Adminer, Mailpit и `phpinfo()`. Подробнее в [docs/04-kullanim.md](docs/04-kullanim.md) (на турецком языке).

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

Чтобы изменить шаблон конфигурации, скопируйте его из `templates\` в `usr\templates\` и редактируйте копию; обновления его не трогают.

## Сборка из исходного кода

Требования: любой .NET SDK 6 или новее (приложение собирается для .NET Framework 4.8, поэтому работает в Windows 7 SP1 – 11 без дополнительных сред выполнения). Если установлен Inno Setup, `build.ps1` также создаёт установщики.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

Архитектура: [docs/02-mimari.md](docs/02-mimari.md), выпуск релизов: [docs/03-yayinlama.md](docs/03-yayinlama.md) (на турецком языке).

## Переводы

Языковые файлы находятся в [`src/Portly/lang`](src/Portly/lang). Скопируйте `en.json`, переведите значения, сохраните плейсхолдеры `{0}` и отправьте pull request. Отсутствующие ключи берутся из английского. См. [CONTRIBUTING.md](CONTRIBUTING.md).

## Конфиденциальность

В Portly нет телеметрии, аналитики и учётных записей. Он подключается к другим системам только для того, что вы сами запускаете или включаете:

- **Проверка обновлений** (включена по умолчанию, отключается в Настройках): загружает каталог пакетов и информацию о релизах из этого репозитория GitHub. Никакие данные о вас или ваших проектах не отправляются.
- **Установка пакета**: загружает его из официального источника (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Публикация проекта в интернете**: запускает `cloudflared` от Cloudflare; посетители попадают на ваш сайт через Cloudflare. Публикация в локальной сети остаётся внутри вашей сети.

Portly меняет системные настройки только с запросом: файл hosts и доверие его локальному центру сертификации запрашивают подтверждение (Windows UAC).

**Удаление**: portable-версия из zip удаляется вместе с папкой; версия с установщиком удаляется через Параметры Windows > Приложения. Чтобы удалить и доверенный центр сертификации Portly, откройте `certmgr.msc` > Доверенные корневые центры сертификации > Сертификаты и удалите "Portly Local CA".

## Политика подписи кода

Релизы для Windows собираются из этого репозитория с помощью GitHub Actions. Подана заявка на подписание кода через SignPath Foundation; до её одобрения релизы не подписаны, а контрольные суммы SHA-256 публикуются вместе с каждым релизом.

Роли в команде:
- Авторы / коммитеры: [Erdem Software](https://github.com/erdemsoftware)
- Рецензенты: [Erdem Software](https://github.com/erdemsoftware)
- Утверждающие (подпись релизов): [Erdem Software](https://github.com/erdemsoftware)

## Поддержать

Portly бесплатен и останется бесплатным: без рекламы, платных тарифов и отслеживания. Если он экономит ваше время, вы можете поддержать его развитие.

- Карта: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID: **50018297**
- Криптовалюта (USDT в сетях TRC-20, BEP-20, Polygon, Solana, Ethereum): в приложении, Меню > Donate

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare и прочие инструменты загружаются из их официальных источников и являются собственностью их владельцев. Portly не связан с Laragon.</sub>
