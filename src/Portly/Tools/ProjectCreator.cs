using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>Quick project: Blank, WordPress (in the UI language) or Laravel.</summary>
    public static class ProjectCreator
    {
        /// <summary>
        /// Terminal based templates: display name, runtime (composer | node) and the command ({0} = folder name).
        /// Node projects run on their own dev server (npm run dev); Portly still serves the folder.
        /// </summary>
        public static readonly List<Tuple<string, string, string, string>> TerminalTemplates = new List<Tuple<string, string, string, string>>
        {
            Tuple.Create("laravel", "Laravel", "composer", "composer create-project laravel/laravel \"{0}\""),
            Tuple.Create("symfony", "Symfony", "composer", "composer create-project symfony/skeleton \"{0}\" && cd \"{0}\" && composer config extra.symfony.allow-contrib true && composer require symfony/apache-pack -n"),
            Tuple.Create("codeigniter", "CodeIgniter 4", "composer", "composer create-project codeigniter4/appstarter \"{0}\""),
            Tuple.Create("cakephp", "CakePHP", "composer", "composer create-project --prefer-dist cakephp/app \"{0}\" -n"),
            Tuple.Create("drupal", "Drupal", "composer", "composer create-project drupal/recommended-project \"{0}\""),
            Tuple.Create("vue", "Vue", "node", "npm create vite@latest \"{0}\" -- --template vue && cd \"{0}\" && npm install"),
            Tuple.Create("react", "React", "node", "npm create vite@latest \"{0}\" -- --template react && cd \"{0}\" && npm install"),
            Tuple.Create("svelte", "Svelte", "node", "npm create vite@latest \"{0}\" -- --template svelte && cd \"{0}\" && npm install"),
            Tuple.Create("nuxt", "Nuxt", "node", "npx --yes nuxi@latest init \"{0}\" --packageManager npm --gitInit false"),
            Tuple.Create("next", "Next.js", "node", "npx --yes create-next-app@latest \"{0}\" --yes"),
            Tuple.Create("angular", "Angular", "node", "npx --yes @angular/cli@latest new \"{0}\" --defaults"),
            Tuple.Create("astro", "Astro", "node", "npm create astro@latest \"{0}\" -- --template basics --install --no-git --yes"),
        };

        public static Tuple<string, string, string, string> TerminalTemplate(string key) { return TerminalTemplates.FirstOrDefault(t => t.Item1 == key); }

        public static string ValidateName(string name) { return ValidateName(name, null); }

        /// <param name="parent">Name of the project the new one goes into (a sub-project), or null.</param>
        public static string ValidateName(string name, string parent)
        {
            if (!Sites.IsValidName(name)) return I18n.T("err.projectName");
            if (Directory.Exists(Path.Combine(BaseDir(parent), name))) return I18n.T("err.projectExists", name);
            return null;
        }

        /// <summary>Folder the project is created in: the projects folder, or the parent project's folder.</summary>
        public static string BaseDir(string parent)
        {
            var root = Settings.Current.DocumentRoot;
            return string.IsNullOrEmpty(parent) ? root : Path.Combine(root, parent);
        }

        /// <summary>Address of a new project: name.tld, or name.parent.tld for a sub-project.</summary>
        public static string HostFor(string name, string parent)
        {
            var host = name.ToLowerInvariant() + "." + (string.IsNullOrEmpty(parent) ? "" : parent.ToLowerInvariant() + ".");
            return host + Settings.Current.Tld;
        }

        public static string DbNameFor(string project) { return project.Replace('-', '_').Replace('/', '_').ToLowerInvariant(); }

        /// <summary>Returns a message for the user (what was done / what to do next).</summary>
        public static Task<string> CreateAsync(string name, string template, bool createDb, Action<string> status, CancellationToken ct)
        {
            return CreateAsync(name, template, createDb, false, status, ct);
        }

        /// <param name="wpFullInstall">WordPress: also run the installer (site title, admin account) with WP-CLI.</param>
        public static Task<string> CreateAsync(string name, string template, bool createDb, bool wpFullInstall, Action<string> status, CancellationToken ct)
        {
            return CreateAsync(name, template, createDb, wpFullInstall, null, status, ct);
        }

        /// <param name="parent">Create it as a sub-project inside this project's folder (address name.parent.tld).</param>
        public static async Task<string> CreateAsync(string name, string template, bool createDb, bool wpFullInstall, string parent, Action<string> status, CancellationToken ct)
        {
            var err = ValidateName(name, parent);
            if (err != null) throw new Exception(err);
            var root = BaseDir(parent);
            Directory.CreateDirectory(root);
            var dir = Path.Combine(root, name);
            // "shop/blog" for a sub-project: its sites.ini section and database name (shop_blog)
            var siteName = string.IsNullOrEmpty(parent) ? name : parent + "/" + name;
            if (!string.IsNullOrEmpty(parent)) Sites.MarkSubProject(parent, name);
            string dbMsg = "";
            if (createDb && template == "wordpress" || createDb && template == "blank")
            {
                var dbErr = Database.CreateDatabase(DbNameFor(siteName));
                dbMsg = dbErr == null ? "\n" + I18n.T("project.dbCreated", DbNameFor(siteName)) : "\n" + I18n.T("project.dbFailed", dbErr);
            }

            switch (template)
            {
                case "wordpress":
                    await CreateWordPress(dir, siteName, status, ct);
                    if (wpFullInstall && createDb && dbMsg.Contains(DbNameFor(siteName)) && ServiceManager.MySql.State == ServiceState.Running)
                    {
                        var creds = await InstallWordPress(dir, name, HostFor(name, parent), status, ct);
                        Log.Info(I18n.T("log.projectCreated", name));
                        return I18n.T("project.created", name) + dbMsg + "\n\n" + creds;
                    }
                    break;
                case "laravel":
                case "symfony":
                case "codeigniter":
                case "cakephp":
                case "drupal":
                case "vue":
                case "react":
                case "svelte":
                case "nuxt":
                case "next":
                case "angular":
                case "astro":
                    var tt = TerminalTemplate(template);
                    if (tt.Item3 == "composer" && (PackageTypes.ActiveDir("php") == null || PackageTypes.ActiveDir("composer") == null))
                        throw new Exception(I18n.T("err.needComposer"));
                    if (tt.Item3 == "node" && PackageTypes.ActiveDir("nodejs") == null)
                        throw new Exception(I18n.T("err.needNode"));
                    if (createDb && tt.Item3 == "composer")
                    {
                        var dbErr2 = Database.CreateDatabase(DbNameFor(siteName));
                        dbMsg = dbErr2 == null ? "\n" + I18n.T("project.dbCreated", DbNameFor(siteName)) : "";
                    }
                    // Node apps (Next, Nuxt, Angular ...) have no index file to recognise them by: keep them listed
                    if (tt.Item3 == "node") Sites.MarkNodeProject(siteName);
                    var after = tt.Item3 == "node" ? " && echo. && echo " + I18n.T("project.nodeDone", name) : " && echo. && echo " + I18n.T("project.laravelDone");
                    Terminal.Run(root, string.Format(tt.Item4, name) + after);
                    return I18n.T("project.composerStarted", tt.Item2) + dbMsg;
                default:
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "index.php"),
                        "<?php\n// " + name + " - created by Portly\n?>\n<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>" + name + "</title></head>\n" +
                        "<body style=\"font-family:Segoe UI,sans-serif;padding:40px\">\n<h1>" + name + "</h1>\n<p>PHP <?= PHP_VERSION ?> · <?= htmlspecialchars($_SERVER['HTTP_HOST'] ?? '') ?></p>\n</body></html>\n",
                        new UTF8Encoding(false));
                    break;
            }
            Log.Info(I18n.T("log.projectCreated", name));
            return I18n.T("project.created", name) + dbMsg;
        }

        static readonly Dictionary<string, string> WpLocales = new Dictionary<string, string>
        {
            { "tr", "tr:tr_TR" }, { "de", "de:de_DE" }, { "es", "es:es_ES" }, { "fr", "fr:fr_FR" }, { "it", "it:it_IT" },
            { "ru", "ru:ru_RU" }, { "pt-BR", "br:pt_BR" }, { "zh-CN", "cn:zh_CN" }, { "ja", "ja:ja" }, { "az", "az:az" },
            { "ar", "ar:ar" }, { "fa", "fa:fa_IR" }, { "ur", "ur:ur" }, { "hi", "hi:hi_IN" }, { "bn", "bn:bn_BD" },
            { "id", "id:id_ID" }, { "ms", "ms:ms_MY" }, { "vi", "vi:vi" }, { "th", "th:th" }, { "ko", "ko:ko_KR" },
            { "pl", "pl:pl_PL" }, { "nl", "nl:nl_NL" }, { "uk", "uk:uk" }, { "cs", "cs:cs_CZ" }, { "ro", "ro:ro_RO" },
            { "hu", "hu:hu_HU" }, { "el", "el:el" }, { "sv", "sv:sv_SE" }, { "bg", "bg:bg_BG" }, { "uz", "uz:uz_UZ" },
        };

        /// <summary>WordPress in the UI language (falls back to English when that build does not exist).</summary>
        static IEnumerable<string> WordPressUrls()
        {
            string v;
            if (WpLocales.TryGetValue(I18n.Code, out v))
            {
                var parts = v.Split(':');
                yield return "https://" + parts[0] + ".wordpress.org/latest-" + parts[1] + ".zip";
            }
            yield return "https://wordpress.org/latest.zip";
        }

        /// <summary>Runs "wp core install" so the site is ready to use (no setup wizard). Returns the login text.</summary>
        static async Task<string> InstallWordPress(string dir, string name, string host, Action<string> status, CancellationToken ct)
        {
            if (PackageTypes.ActiveDir("wpcli") == null)
            {
                var entry = Catalog.Recommended("wpcli");
                if (entry == null) return "";
                status(I18n.T("log.downloading", "WP-CLI"));
                await Installer.InstallAsync(entry, null, ct);
            }
            status(I18n.T("project.wpInstalling"));
            var php = Path.Combine(PackageTypes.ActiveDir("php"), "php.exe");
            var phar = Path.Combine(PackageTypes.ActiveDir("wpcli"), "wp-cli.phar");
            var url = new Config.Site { Host = host }.PreferredUrl.TrimEnd('/');
            var password = RandomSalt().Replace("\"", "").Replace("'", "").Replace("%", "").Replace("^", "").Replace("&", "").Replace("<", "").Replace(">", "").Replace("|", "").Substring(0, 16);
            // WP-CLI bundles libraries that print PHP 8.5 deprecation notices: keep them out of the result
            var args = "-d error_reporting=24575 -d display_errors=stderr \"" + phar + "\" core install --path=\"" + dir + "\" --url=\"" + url + "\" --title=\"" + name + "\" --admin_user=admin --admin_password=\"" + password + "\" --admin_email=admin@" + host + " --skip-email";
            var r = await Task.Run(() => ProcessUtil.Run(php, args, dir, null, 180000), ct);
            if (!r.Ok && !r.Output.Contains("Success")) { Log.Warn("wp core install: " + r.Output); return I18n.T("project.wpManual", url + "/wp-admin/install.php"); }
            WriteWordPressCaPlugin(dir);
            return I18n.T("project.wpReady", url + "/wp-admin/", "admin", password);
        }

        /// <summary>
        /// WordPress uses its own CA list, so loopback requests (Site Health, cron, REST) to https://*.portly fail
        /// with cURL error 60. A tiny must-use plugin adds Portly's bundle (Mozilla roots + Portly CA).
        /// </summary>
        public static void WriteWordPressCaPlugin(string dir)
        {
            try
            {
                var mu = Path.Combine(dir, "wp-content", "mu-plugins");
                Directory.CreateDirectory(mu);
                var bundle = Paths.Fwd(Certs.BundleFile).Replace("'", "");
                var php = new StringBuilder();
                php.AppendLine("<?php");
                php.AppendLine("// Added by Portly: trust the local development certificates (https://*." + Settings.Current.Tld + ").");
                php.AppendLine("// Safe to delete on a live server; it does nothing when the file below does not exist.");
                php.AppendLine("add_filter('http_request_args', function ($args) {");
                php.AppendLine("    $bundle = '" + bundle + "';");
                php.AppendLine("    if (is_file($bundle)) { $args['sslcertificates'] = $bundle; }");
                php.AppendLine("    return $args;");
                php.AppendLine("});");
                php.AppendLine("// Shared with \"Share on the internet\" or on the local network: answer with the public address instead of redirecting to ." + Settings.Current.Tld);
                php.AppendLine("$portlyHost = isset($_SERVER['HTTP_X_FORWARDED_HOST']) ? $_SERVER['HTTP_X_FORWARDED_HOST'] : '';");
                php.AppendLine("if (preg_match('/^[a-z0-9-]+\\.trycloudflare\\.com$/i', $portlyHost)) {");
                php.AppendLine("    $portlyUrl = 'https://' . $portlyHost;");
                php.AppendLine("    add_filter('option_home', function () use ($portlyUrl) { return $portlyUrl; });");
                php.AppendLine("    add_filter('option_siteurl', function () use ($portlyUrl) { return $portlyUrl; });");
                php.AppendLine("    $_SERVER['HTTPS'] = 'on';");
                php.AppendLine("} elseif (preg_match('/^(?=(10|172\\.(1[6-9]|2[0-9]|3[01])|192\\.168)\\.)[0-9]{1,3}(\\.[0-9]{1,3}){3}:[0-9]+$/', $portlyHost)) {");
                php.AppendLine("    // shared on the local network (phone, tablet): http://192.168.x.x:port");
                php.AppendLine("    $portlyUrl = 'http://' . $portlyHost;");
                php.AppendLine("    add_filter('option_home', function () use ($portlyUrl) { return $portlyUrl; });");
                php.AppendLine("    add_filter('option_siteurl', function () use ($portlyUrl) { return $portlyUrl; });");
                php.AppendLine("}");
                File.WriteAllText(Path.Combine(mu, "portly-local-ca.php"), php.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("mu-plugin: " + ex.Message); }
        }

        static async Task CreateWordPress(string dir, string name, Action<string> status, CancellationToken ct)
        {
            var zip = Path.Combine(Paths.Downloads, "wordpress-" + I18n.Code + ".zip");
            var fresh = File.Exists(zip) && File.GetLastWriteTime(zip) > DateTime.Now.AddDays(-7);
            if (!fresh)
            {
                Exception last = null;
                foreach (var url in WordPressUrls())
                {
                    try
                    {
                        int lastShown = -2;
                        await Net.DownloadFileAsync(url, zip, (p, b) =>
                        {
                            var key = p < 0 ? (int)(b / 1048576) : p;
                            if (key == lastShown) return;
                            lastShown = key;
                            status(I18n.T("project.downloading", "WordPress", p < 0 ? key + " MB" : p + "%"));
                        }, ct);
                        last = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { last = ex; Log.Warn(url + " -> " + ex.Message); }
                }
                if (last != null) throw last;
            }
            status(I18n.T("project.extracting"));
            var tmp = Path.Combine(Paths.Tmp, "extract", "wp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, tmp), ct);
            var src = Path.Combine(tmp, "wordpress");
            if (!Directory.Exists(src)) src = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;
            Installer.MoveDir(src, dir);
            try { Directory.Delete(tmp, true); } catch { }

            var sample = Path.Combine(dir, "wp-config-sample.php");
            if (File.Exists(sample))
            {
                var port = Settings.Current.MysqlPort;
                // localized packages translate the placeholders ("veritabaniismi", "eşsiz karakter kümenizi..."),
                // so set the values by constant name instead of replacing the English texts
                var cfg = File.ReadAllText(sample, Encoding.UTF8);
                cfg = DefineValue(cfg, "DB_NAME", DbNameFor(name));
                cfg = DefineValue(cfg, "DB_USER", "root");
                cfg = DefineValue(cfg, "DB_PASSWORD", Settings.Current.MysqlRootPassword);
                cfg = DefineValue(cfg, "DB_HOST", "127.0.0.1" + (port == 3306 ? "" : ":" + port));
                foreach (var key in new[] { "AUTH_KEY", "SECURE_AUTH_KEY", "LOGGED_IN_KEY", "NONCE_KEY", "AUTH_SALT", "SECURE_AUTH_SALT", "LOGGED_IN_SALT", "NONCE_SALT" })
                    cfg = DefineValue(cfg, key, RandomSalt());
                File.WriteAllText(Path.Combine(dir, "wp-config.php"), cfg, new UTF8Encoding(false));
            }
            WriteWordPressCaPlugin(dir);
        }

        /// <summary>define( 'NAME', '...' ) -> define( 'NAME', 'value' ), the value escaped for a PHP single-quoted string.</summary>
        static string DefineValue(string cfg, string constant, string value)
        {
            var php = (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
            return System.Text.RegularExpressions.Regex.Replace(cfg,
                @"(define\(\s*'" + constant + @"'\s*,\s*')(?:[^'\\]|\\.)*(')",
                m => m.Groups[1].Value + php + m.Groups[2].Value);
        }

        static string RandomSalt()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#%^&*()-_=+[]{};:,.<>?";
            var b = new byte[64];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            var sb = new StringBuilder(64);
            foreach (var x in b) sb.Append(chars[x % chars.Length]);
            return sb.ToString();
        }

        /// <summary>Move a project to the Recycle Bin (recoverable).</summary>
        public static void DeleteToRecycleBin(Site site)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(site.Folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log.Info(I18n.T("log.projectDeleted", site.Name));
        }
    }
}
