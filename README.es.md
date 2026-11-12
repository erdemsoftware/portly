<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Entorno de desarrollo web local, rápido, portátil y de código abierto para Windows<br>
  <sub>por <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · sin anuncios, sin rastreo</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <b>Español</b> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>La aplicación está disponible en 31 idiomas (árabe, persa y urdu, de derecha a izquierda).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="Servicios"> <img src="assets/screenshots/en-projects.png" width="49%" alt="Proyectos"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="Abrir un proyecto en el teléfono con un código QR"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="Nuevo proyecto"></p>

Portly ejecuta Apache o Nginx, PHP, MySQL o MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer y phpMyAdmin desde una sola carpeta. No necesita instalador y no instala nada en Windows por defecto: no hay servicios de Windows (un modo de servicio opcional, desactivado salvo que lo actives en Configuración > Funciones, puede ejecutar la pila como servicio) ni restos en el registro. Incluso funciona desde una memoria USB.

## ¿Por qué Portly?

| | Laragon | **Portly** |
|---|---|---|
| Licencia | Código cerrado, de pago para uso comercial | **MIT, totalmente gratuito, sin anuncios** |
| Actualizaciones de la app | Descargar un nuevo instalador | **Un clic dentro de la app** (verificado con SHA-256) |
| Lista de paquetes | `packages.conf` local | **Catálogo actualizado remotamente** + versiones de PHP en vivo desde windows.php.net |
| Versión de PHP por sitio | No | **Sí** (cada versión de PHP en su propio puerto FastCGI) |
| HTTPS | Un certificado, recreado por proyecto | **CA raíz local** de confianza una sola vez, un certificado por sitio |
| Seguridad de red | Escucha en todas las interfaces | **127.0.0.1 por defecto** (sin aviso del firewall) |
| Endurecimiento de PHP | Valores por defecto | `session.upload_progress` y `allow_url_include` **desactivados** |
| Seguridad ante cierres | — | Si Portly se cierra, Windows detiene los servidores que inició (job object) |
| Conflictos de puertos | Mensaje de error | Muestra el programa que ocupa el puerto; **cambia a un puerto libre con un clic** o cierra ese programa |
| Compartir | ngrok (requiere cuenta) | **Red local** (teléfono / tablet en la misma Wi-Fi, código QR, https, contraseña) e **Internet** mediante Cloudflare: dirección aleatoria sin cuenta, o una **dirección fija en tu propio dominio** |
| Migración | — | Proyectos y bases de datos desde **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP** (proyectos desde Herd y Local) |
| Bases de datos | MySQL | **MySQL o MariaDB, PostgreSQL, MongoDB, SQLite**; Redis y Memcached |
| Herramientas de base de datos | HeidiSQL | **phpMyAdmin** (se abre con sesión iniciada mientras root no tenga contraseña), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Manual | **WordPress con un clic** (WP-CLI, usuario administrador listo) |
| Idiomas | 34 (txt) | **31**, JSON simple, abierto a contribuciones |
| Windows 7 / 8 / 8.1 | Poco claro | **Edición Legacy separada** |

## Características

- **Proyectos**: cada carpeta dentro de `www` se convierte en `https://nombre.portly`; las carpetas `public` estilo Laravel se detectan. Agrupa carpetas y subproyectos (`blog.shop.portly`), con plantillas para Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular y Astro. Cada tarjeta muestra con qué está construido el proyecto y su base de datos.
- **Compartir un proyecto**
  - *Red local*: ábrelo en tu teléfono con un código QR. Funciona sobre https; una guía en el teléfono instala el certificado de Portly en iPhone y Android una sola vez. Contraseña opcional, límite de tiempo, inicio con Portly y lista de dispositivos conectados; los errores de PHP desde el teléfono aparecen en el registro de Portly.
  - *Internet*: un túnel rápido gratuito de Cloudflare (sin cuenta), o tu propio dominio como `demo.example.com` a través de tu cuenta de Cloudflare. Misma contraseña, límite de tiempo y lista de dispositivos.
- **Versión de PHP por sitio**, **extensiones de PHP** con un clic (17 extensiones PECL, verificadas por versión de PHP), **Xdebug**, terminales que siguen la versión de PHP / Node del proyecto (`.php-version`, `composer.json`, `.nvmrc`).
- **Servicios**: Iniciar todo / Detener todo, con un candado por servicio (elige cuáles pertenecen al grupo), MySQL / MariaDB en una sola tarjeta y Tomcat + Java opcional.
- **Avisos de actualización** («PHP 8.6 ya está disponible» → instalar y cambiar con un clic), avisos de fin de vida útil y de CVE.
- **Seguridad de bases de datos**: comprobar y reparar, traslados seguros entre versiones de MySQL / MariaDB, importación de SQL grandes, copias de seguridad automáticas, restablecimiento de la contraseña de root olvidada y modo de recuperación de InnoDB.
- **Flujo de trabajo para desarrolladores**: tareas en segundo plano (cola, planificador, Vite), registros del sitio, perfilador, CLI `portly` con hooks, archivo de proyecto `portly.yml`, envío / descarga por SSH.
- **Sitios**: dominios adicionales y comodín, certificados propios, archivado, Corregir SSL, confianza de certificados en Firefox / Java, **Caddy** y **FrankenPHP** (Octane).
- **Prevención de problemas**: carpetas de OneDrive, rutas largas y con caracteres no ASCII, un segundo PHP o PHPRC, antivirus que bloquea el archivo hosts y comprobación de cuarentena.
- Interfaz moderna con barra lateral y tema claro / oscuro que sigue a Windows, DPI por monitor y compatibilidad con lectores de pantalla. Las funciones poco usadas se activan en Configuración > Funciones.

## Ediciones

| Descarga | Windows | Actualizaciones | Paquetes |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64 bits) | **Sí**: app + catálogo de paquetes | Últimas: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64 bits) | **No** (catálogo congelado) | Últimas versiones que funcionan en Windows 7: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Beta**: consulta [Portly 2 beta](#portly-2-beta) más abajo. |

La edición Legacy necesita .NET Framework 4.8 (ya incluido en Windows 10 / 11).

<a id="portly-2-beta"></a>

## Portly 2 beta: macOS, Linux y una nueva app para Windows

Portly 2 funciona en **macOS (Apple Silicon e Intel), Linux (x64 y arm64) y Windows 10 / 11**, con el mismo número de versión en todas partes. Está en beta: Portly 1.x sigue siendo la versión estable y no se actualiza a esta por sí sola.

- **macOS / Linux**, una sola línea, sin sudo (verificado con SHA-256; macOS: `/Applications/Portly.app`, Linux: `~/.local/share/portly`):

  ```sh
  curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
  ```

  O descarga el `.dmg`, `.AppImage`, `.deb` o `.tar.gz` desde [Releases](https://github.com/erdemsoftware/portly/releases).
- **Windows 10 / 11**: `Portly-2.x.y-beta.n-windows-x64.zip` desde la misma página, descomprimido en una carpeta nueva.
- En macOS / Linux: PHP con Xdebug, Caddy, Apache, Nginx, MySQL, MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Mailpit y Node.js en `~/Portly`; los proyectos se abren en `https://nombre.localhost`.

## Inicio rápido

1. Descarga un zip desde [Releases](https://github.com/erdemsoftware/portly/releases) y descomprímelo en una carpeta **sin espacios**, por ejemplo `C:\Portly`.
2. Ejecuta `Portly.exe`. Los paquetes recomendados vienen preseleccionados en el primer inicio: haz clic en **Instalar seleccionados**.
3. Haz clic en **Iniciar todo**.
4. Crea una carpeta en `www`: `www\shop` se convierte en `https://shop.portly`. Una carpeta con nombre de dominio (`www\example.com`) se abre como `https://example.com`, así que las URL no cambian al publicar; un interruptor Local / Live en su tarjeta devuelve el sitio en vivo a tu PC, mientras la copia local sigue funcionando en `https://example.com.portly`.

`http://localhost` abre un panel con todos los sitios, Adminer, Mailpit y `phpinfo()`. Más información en [docs/04-kullanim.md](docs/04-kullanim.md) (en turco).

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

Para cambiar una plantilla de configuración, cópiala de `templates\` a `usr\templates\` y edita la copia; las actualizaciones no la tocan.

## Compilar desde el código fuente

Requisitos: cualquier .NET SDK 6 o más reciente (la aplicación apunta a .NET Framework 4.8, por lo que funciona en Windows 7 SP1 – 11 sin runtimes adicionales). Si Inno Setup está instalado, `build.ps1` también genera los programas de instalación.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

Arquitectura: [docs/02-mimari.md](docs/02-mimari.md), publicación: [docs/03-yayinlama.md](docs/03-yayinlama.md) (en turco).

## Traducciones

Los archivos de idioma están en [`src/Portly/lang`](src/Portly/lang). Copia `en.json`, traduce los valores, conserva los marcadores `{0}` y envía un pull request. Las claves que falten usan el inglés como respaldo. Consulta [CONTRIBUTING.md](CONTRIBUTING.md).

## Privacidad

Portly no tiene telemetría, analíticas ni cuentas. Solo se conecta a otros sistemas para lo que tú inicies o actives:

- **Comprobación de actualizaciones** (activada por defecto, se puede desactivar en Configuración): descarga el catálogo de paquetes y la información de versiones desde este repositorio de GitHub. No se envía nada sobre ti ni sobre tus proyectos.
- **Instalación de un paquete**: se descarga desde su fuente oficial (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Compartir un proyecto en Internet**: ejecuta `cloudflared` de Cloudflare; los visitantes llegan a tu sitio a través de Cloudflare. Compartir en la red local se queda dentro de tu red.

Portly solo cambia la configuración del sistema con una confirmación: el archivo hosts y la confianza en su autoridad de certificados local piden confirmación (UAC de Windows).

**Desinstalación**: la versión portable en zip se elimina borrando su carpeta; la versión con instalador se desinstala desde Configuración de Windows > Aplicaciones. Para quitar también la autoridad de certificados de Portly de confianza, abre `certmgr.msc` > Entidades de certificación raíz de confianza > Certificados y elimina "Portly Local CA".

## Política de firma de código

Las versiones para Windows se compilan desde este repositorio mediante GitHub Actions. La firma de código a través de SignPath Foundation está en trámite; hasta entonces las versiones no están firmadas y sus sumas SHA-256 se publican con cada versión.

Roles del equipo:
- Autores / committers: [Erdem Software](https://github.com/erdemsoftware)
- Revisores: [Erdem Software](https://github.com/erdemsoftware)
- Aprobadores (firma de versiones): [Erdem Software](https://github.com/erdemsoftware)

## Donar

Portly es gratuito y lo seguirá siendo: sin anuncios, sin planes de pago y sin rastreo. Si te ahorra tiempo, puedes apoyar su desarrollo.

- Tarjeta: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- ID de Binance Pay: **50018297**
- Cripto (USDT en TRC-20, BEP-20, Polygon, Solana, Ethereum): en la app, Menú > Donar

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare y las demás herramientas se descargan de sus fuentes oficiales y son propiedad de sus respectivos dueños. Portly no está afiliado a Laragon.</sub>
