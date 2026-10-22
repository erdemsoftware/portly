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
        public static readonly string[] Templates = { "blank", "wordpress", "laravel", "symfony", "codeigniter", "cakephp", "drupal" };

        /// <summary>Composer based templates: display name and the command run in a terminal ({0} = folder name).</summary>
        public static readonly Dictionary<string, Tuple<string, string>> ComposerTemplates = new Dictionary<string, Tuple<string, string>>
        {
            { "laravel", Tuple.Create("Laravel", "composer create-project laravel/laravel \"{0}\"") },
            { "symfony", Tuple.Create("Symfony", "composer create-project symfony/skeleton \"{0}\" && cd \"{0}\" && composer config extra.symfony.allow-contrib true && composer require symfony/apache-pack -n") },
            { "codeigniter", Tuple.Create("CodeIgniter 4", "composer create-project codeigniter4/appstarter \"{0}\"") },
            { "cakephp", Tuple.Create("CakePHP", "composer create-project --prefer-dist cakephp/app \"{0}\" -n") },
            { "drupal", Tuple.Create("Drupal", "composer create-project drupal/recommended-project \"{0}\"") },
        };

        public static string ValidateName(string name)
        {
            if (!Sites.IsValidName(name)) return I18n.T("err.projectName");
            if (Directory.Exists(Path.Combine(Settings.Current.DocumentRoot, name))) return I18n.T("err.projectExists", name);
            return null;
        }

        public static string DbNameFor(string project) { return project.Replace('-', '_').Replace('/', '_').ToLowerInvariant(); }

        /// <summary>Returns a message for the user (what was done / what to do next).</summary>
        public static async Task<string> CreateAsync(string name, string template, bool createDb, Action<string> status, CancellationToken ct)
        {
            var err = ValidateName(name);
            if (err != null) throw new Exception(err);
            var root = Settings.Current.DocumentRoot;
            Directory.CreateDirectory(root);
            var dir = Path.Combine(root, name);
            string dbMsg = "";
            if (createDb && template == "wordpress" || createDb && template == "blank")
            {
                var dbErr = Database.CreateDatabase(DbNameFor(name));
                dbMsg = dbErr == null ? "\n" + I18n.T("project.dbCreated", DbNameFor(name)) : "\n" + I18n.T("project.dbFailed", dbErr);
            }

            switch (template)
            {
                case "wordpress":
                    await CreateWordPress(dir, name, status, ct);
                    break;
                case "laravel":
                case "symfony":
                case "codeigniter":
                case "cakephp":
                case "drupal":
                    if (PackageTypes.ActiveDir("php") == null || PackageTypes.ActiveDir("composer") == null)
                        throw new Exception(I18n.T("err.needComposer"));
                    if (createDb)
                    {
                        var dbErr2 = Database.CreateDatabase(DbNameFor(name));
                        dbMsg = dbErr2 == null ? "\n" + I18n.T("project.dbCreated", DbNameFor(name)) : "";
                    }
                    Terminal.Run(root, string.Format(ComposerTemplates[template].Item2, name) + " && echo. && echo " + I18n.T("project.laravelDone"));
                    return I18n.T("project.composerStarted", ComposerTemplates[template].Item1) + dbMsg;
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
                        await Net.DownloadFileAsync(url, zip, (p, b) => status(I18n.T("project.downloading", "WordPress", p < 0 ? (b / 1048576) + " MB" : p + "%")), ct);
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
                var cfg = File.ReadAllText(sample, Encoding.UTF8)
                    .Replace("database_name_here", DbNameFor(name))
                    .Replace("username_here", "root")
                    .Replace("password_here", Settings.Current.MysqlRootPassword.Replace("\\", "\\\\").Replace("'", "\\'"))
                    .Replace("'localhost'", "'127.0.0.1" + (port == 3306 ? "" : ":" + port) + "'");
                while (cfg.Contains("put your unique phrase here"))
                {
                    int i = cfg.IndexOf("put your unique phrase here", StringComparison.Ordinal);
                    cfg = cfg.Substring(0, i) + RandomSalt() + cfg.Substring(i + "put your unique phrase here".Length);
                }
                File.WriteAllText(Path.Combine(dir, "wp-config.php"), cfg, new UTF8Encoding(false));
            }
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
