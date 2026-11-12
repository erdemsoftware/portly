<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Ambiente de desenvolvimento web local, rápido, portátil e de código aberto para Windows<br>
  <sub>por <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · sem anúncios, sem rastreamento</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <b>Português (BR)</b> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>O próprio aplicativo está disponível em 31 idiomas (árabe, persa e urdu, escritos da direita para a esquerda).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="Serviços"> <img src="assets/screenshots/en-projects.png" width="49%" alt="Projetos"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="Abra um projeto no celular com um código QR"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="Novo projeto"></p>

Portly executa Apache ou Nginx, PHP, MySQL ou MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer e phpMyAdmin a partir de uma única pasta. Não é preciso instalador e, por padrão, nada é instalado no Windows: sem serviços do Windows (um modo de serviço opcional, desativado a menos que você o ative em Configurações > Recursos, pode executar a pilha como serviço), sem sujeira no registro. Funciona até a partir de um pen drive USB.

## Por que o Portly?

| | Laragon | **Portly** |
|---|---|---|
| Licença | Código fechado, pago para uso comercial | **MIT, totalmente gratuito, sem anúncios** |
| Atualizações do aplicativo | Baixar um novo instalador | **Um clique dentro do aplicativo** (verificado por SHA-256) |
| Lista de pacotes | `packages.conf` local | **Catálogo atualizado remotamente** + versões do PHP atualizadas a partir de windows.php.net |
| Versão do PHP por site | Não | **Sim** (cada versão do PHP em sua própria porta FastCGI) |
| HTTPS | Um certificado, recriado a cada projeto | **CA raiz local** confiada uma vez, um certificado por site |
| Segurança de rede | Escuta em todas as interfaces | **127.0.0.1 por padrão** (sem aviso do firewall) |
| Endurecimento do PHP | Padrões | `session.upload_progress` e `allow_url_include` **desativados** |
| Segurança em falhas | — | Se o Portly travar, o Windows para os servidores que ele iniciou (job object) |
| Conflitos de porta | Mensagem de erro | Mostra o programa que está usando a porta; **troque para uma porta livre com um clique** ou feche esse programa |
| Compartilhamento | ngrok (é preciso conta) | **Rede local** (celular / tablet na mesma Wi-Fi, código QR, https, senha) e **internet** pela Cloudflare: endereço aleatório sem conta, ou um **endereço fixo no seu próprio domínio** |
| Migração | — | Projetos e bancos de dados do **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP** (projetos do Herd e do Local) |
| Bancos de dados | MySQL | **MySQL ou MariaDB, PostgreSQL, MongoDB, SQLite**; Redis e Memcached |
| Ferramentas de banco | HeidiSQL | **phpMyAdmin** (abre já logado enquanto o root não tiver senha), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Manual | **WordPress com um clique** (WP-CLI, usuário administrador pronto) |
| Idiomas | 34 (txt) | **31**, JSON simples, aberto a contribuições |
| Windows 7 / 8 / 8.1 | Incerto | **Edição Legacy separada** |

## Recursos

- **Projetos**: cada pasta em `www` vira `https://nome.portly`; pastas no estilo Laravel com `public` são detectadas. Pastas de grupo e subprojetos (`blog.shop.portly`), modelos para Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular e Astro. Cada cartão mostra com o que o projeto foi feito e o seu banco de dados.
- **Compartilhar um projeto**
  - *Rede local*: abra no celular com um código QR. Funciona por https; um guia no celular instala o certificado do Portly no iPhone e no Android uma única vez. Senha opcional, limite de tempo, iniciar com o Portly, lista de dispositivos conectados; erros de PHP vindos do celular aparecem no log do Portly.
  - *Internet*: um túnel rápido gratuito da Cloudflare (sem conta), ou o seu próprio domínio, como `demo.example.com`, pela sua conta Cloudflare. Mesma senha, limite de tempo e lista de dispositivos.
- **Versão do PHP por site**, **extensões PHP** com um clique (17 extensões PECL, verificadas para cada versão do PHP), **Xdebug**, terminais que acompanham a versão do PHP / Node do projeto (`.php-version`, `composer.json`, `.nvmrc`).
- **Serviços**: Iniciar tudo / Parar tudo com um cadeado em cada serviço (escolha quais pertencem ao grupo), MySQL / MariaDB em um só cartão, Tomcat + Java opcional.
- **Avisos de atualização** ("PHP 8.6 foi lançado" → instalar e trocar com um clique), avisos de fim de suporte e de CVE.
- **Segurança do banco de dados**: verificar e reparar, mudanças seguras entre versões do MySQL / MariaDB, importação de SQL grande, backups automáticos, redefinição de senha root esquecida, modo de recuperação do InnoDB.
- **Fluxo de trabalho do desenvolvedor**: tarefas em segundo plano (fila, agendador, Vite), logs do site, profiler, CLI `portly` com hooks, arquivo de projeto `portly.yml`, push / pull via SSH.
- **Sites**: domínios extras e curinga, seus próprios certificados, arquivo, Corrigir SSL, confiança de certificado no Firefox / Java, **Caddy** e **FrankenPHP** (Octane).
- **Prevenção de problemas**: pastas do OneDrive, caminhos não ASCII e longos, um segundo PHP ou PHPRC, antivírus bloqueando o arquivo hosts, verificação de quarentena.
- Interface moderna em barra lateral com tema claro / escuro que segue o Windows, DPI por monitor e suporte a leitores de tela. Recursos pouco usados são ativados em Configurações > Recursos.

## Edições

| Download | Windows | Atualizações | Pacotes |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64 bits) | **Sim**: aplicativo + catálogo de pacotes | Mais recentes: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64 bits) | **Não** (catálogo congelado) | Últimas versões que rodam no Windows 7: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Beta**: veja [Portly 2 beta](#portly-2-beta) abaixo. |

A edição Legacy precisa do .NET Framework 4.8 (já incluído no Windows 10 / 11).

<a id="portly-2-beta"></a>

## Portly 2 beta: macOS, Linux e um novo aplicativo para Windows

O Portly 2 roda em **macOS (Apple Silicon e Intel), Linux (x64 e arm64) e Windows 10 / 11**, com o mesmo número de versão em todos. Está em beta: o Portly 1.x continua sendo a versão estável e não é atualizado para esta automaticamente.

- **macOS / Linux**, uma linha, sem sudo (verificado por SHA-256; macOS: `/Applications/Portly.app`, Linux: `~/.local/share/portly`):

  ```sh
  curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
  ```

  Ou baixe o `.dmg`, `.AppImage`, `.deb` ou `.tar.gz` em [Releases](https://github.com/erdemsoftware/portly/releases).
- **Windows 10 / 11**: `Portly-2.x.y-beta.n-windows-x64.zip` da mesma página, descompactado em uma nova pasta.
- No macOS / Linux: PHP com Xdebug, Caddy, Apache, Nginx, MySQL, MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Mailpit e Node.js em `~/Portly`; os projetos abrem em `https://nome.localhost`.

## Início rápido

1. Baixe um zip em [Releases](https://github.com/erdemsoftware/portly/releases) e descompacte-o em uma pasta **sem espaços**, por exemplo `C:\Portly`.
2. Execute `Portly.exe`. Os pacotes recomendados já vêm marcados na primeira execução: clique em **Instalar selecionados**.
3. Clique em **Iniciar tudo**.
4. Crie uma pasta em `www`: `www\shop` vira `https://shop.portly`. Uma pasta com nome de domínio (`www\example.com`) abre como `https://example.com`, então os endereços permanecem os mesmos ao publicar; um seletor Local / Live no cartão alterna para o site publicado no seu PC, enquanto a cópia local continua funcionando em `https://example.com.portly`.

`http://localhost` abre um painel com todos os sites, Adminer, Mailpit e `phpinfo()`. Mais detalhes em [docs/04-kullanim.md](docs/04-kullanim.md) (em turco).

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

Para alterar um modelo de configuração, copie-o de `templates\` para `usr\templates\` e edite a cópia; as atualizações não o modificam.

## Compilar a partir do código-fonte

Requisitos: qualquer .NET SDK 6 ou mais recente (o aplicativo tem como alvo o .NET Framework 4.8, então roda do Windows 7 SP1 ao 11 sem runtimes extras). Com o Inno Setup instalado, o `build.ps1` também gera os programas de instalação.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

Arquitetura: [docs/02-mimari.md](docs/02-mimari.md), publicação: [docs/03-yayinlama.md](docs/03-yayinlama.md) (em turco).

## Traduções

Os arquivos de idioma ficam em [`src/Portly/lang`](src/Portly/lang). Copie `en.json`, traduza os valores, mantenha os placeholders `{0}` e envie um pull request. Chaves ausentes usam o inglês como alternativa. Veja [CONTRIBUTING.md](CONTRIBUTING.md).

## Privacidade

O Portly não tem telemetria, nem análises, nem contas. Ele só se conecta a outros sistemas para coisas que você inicia ou ativa:

- **Verificação de atualizações** (ativada por padrão, pode ser desligada em Configurações): baixa o catálogo de pacotes e as informações de versão deste repositório do GitHub. Nada sobre você ou seus projetos é enviado.
- **Instalação de um pacote**: baixa da fonte oficial (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Compartilhamento de um projeto na internet**: executa o `cloudflared` da Cloudflare; os visitantes acessam seu site pela Cloudflare. O compartilhamento em rede local permanece dentro da sua rede.

O Portly altera configurações do sistema somente com aviso: o arquivo hosts e a confiança na sua autoridade certificadora local pedem confirmação (UAC do Windows).

**Desinstalação**: a versão portátil em zip é removida apagando a sua pasta; a versão com instalador é desinstalada em Configurações do Windows > Aplicativos. Para remover também a autoridade certificadora confiável do Portly, abra `certmgr.msc` > Autoridades de Certificação Raiz Confiáveis > Certificados e exclua "Portly Local CA".

## Política de assinatura de código

As versões para Windows são compiladas a partir deste repositório pelo GitHub Actions. A assinatura de código pela SignPath Foundation foi solicitada; até lá, as versões não são assinadas e as somas SHA-256 são publicadas com cada versão.

Funções da equipe:
- Autores / committers: [Erdem Software](https://github.com/erdemsoftware)
- Revisores: [Erdem Software](https://github.com/erdemsoftware)
- Aprovadores (assinatura das versões): [Erdem Software](https://github.com/erdemsoftware)

## Doar

O Portly é gratuito e continuará sendo: sem anúncios, sem planos pagos, sem rastreamento. Se ele economiza seu tempo, você pode apoiar o seu desenvolvimento.

- Cartão: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID: **50018297**
- Cripto (USDT nas redes TRC-20, BEP-20, Polygon, Solana, Ethereum): no aplicativo, Menu > Doar

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare e as demais ferramentas são baixados de suas fontes oficiais e são propriedade de seus respectivos donos. O Portly não é afiliado ao Laragon.</sub>
