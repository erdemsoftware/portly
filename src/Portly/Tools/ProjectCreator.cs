using System;
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
        public static readonly string[] Templates = { "blank", "wordpress", "laravel" };

        public static string ValidateName(string name)
        {
            if (!Sites.IsValidName(name)) return I18n.T("err.projectName");
            if (Directory.Exists(Path.Combine(Settings.Current.DocumentRoot, name))) return I18n.T("err.projectExists", name);
            return null;
        }

        public static string DbNameFor(string project) { return project.Replace('-', '_').ToLowerInvariant(); }

        /// <summary>Returns a message for the user (what was done / what to do next).</summary>
        public static async Task<string> CreateAsync(string name, string template, bool createDb, Action<string> status, CancellationToken ct)
        {
            var err = ValidateName(name);
            if (err != null) throw new Exception(err);
            var root = Settings.Current.DocumentRoot;
            Directory.CreateDirectory(root);
            var dir = Path.Combine(root, name);
            string dbMsg = "";
            if (createDb && template != "laravel")
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
                    if (PackageTypes.ActiveDir("php") == null || PackageTypes.ActiveDir("composer") == null)
                        throw new Exception(I18n.T("err.needComposer"));
                    Terminal.Run(root, "composer create-project laravel/laravel \"" + name + "\" && echo. && echo " + I18n.T("project.laravelDone"));
                    return I18n.T("project.laravelStarted");
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

        static string WordPressUrl()
        {
            switch (I18n.Code)
            {
                case "tr": return "https://tr.wordpress.org/latest-tr_TR.zip";
                case "de": return "https://de.wordpress.org/latest-de_DE.zip";
                case "es": return "https://es.wordpress.org/latest-es_ES.zip";
                case "fr": return "https://fr.wordpress.org/latest-fr_FR.zip";
                case "it": return "https://it.wordpress.org/latest-it_IT.zip";
                case "ru": return "https://ru.wordpress.org/latest-ru_RU.zip";
                case "pt-BR": return "https://br.wordpress.org/latest-pt_BR.zip";
                case "zh-CN": return "https://cn.wordpress.org/latest-zh_CN.zip";
                case "ja": return "https://ja.wordpress.org/latest-ja.zip";
                case "az": return "https://az.wordpress.org/latest-az.zip";
                default: return "https://wordpress.org/latest.zip";
            }
        }

        static async Task CreateWordPress(string dir, string name, Action<string> status, CancellationToken ct)
        {
            var zip = Path.Combine(Paths.Downloads, "wordpress-" + I18n.Code + ".zip");
            var fresh = File.Exists(zip) && File.GetLastWriteTime(zip) > DateTime.Now.AddDays(-7);
            if (!fresh)
            {
                await Net.DownloadFileAsync(WordPressUrl(), zip, (p, b) => status(I18n.T("project.downloading", "WordPress", p < 0 ? (b / 1048576) + " MB" : p + "%")), ct);
            }
            status(I18n.T("project.extracting"));
            var tmp = Path.Combine(Paths.Tmp, "extract", "wp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, tmp), ct);
            var src = Path.Combine(tmp, "wordpress");
            if (!Directory.Exists(src)) src = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;
            Directory.Move(src, dir);
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
