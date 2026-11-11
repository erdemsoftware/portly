using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>
    /// Version-following commands for every terminal: bin\shims holds php / composer / wp / node / npm / npx as .cmd
    /// (cmd, PowerShell) and as extension-less bash scripts (Git Bash). The folder never changes, so a terminal that
    /// was opened before a version switch still runs the right version. The scripts only read small map files that
    /// Portly rewrites whenever sites, versions or settings change; no .NET program starts per call.
    ///
    ///   PHP:  .php-version (current folder or a parent) > Portly site (sites.ini php=) > composer.json "require.php"
    ///         (newest installed version that fits; computed here) > Portly's default PHP.
    ///   Node: (feature "nodeVersions") .nvmrc / .node-version > package.json "engines.node" > active Node.
    ///         npm's global prefix goes to usr\npm\node&lt;major&gt; so global tools stay with their Node version.
    ///
    /// Batch files are read in the console's OEM code page, so the .cmd map is written in that code page
    /// (a project path with letters the code page lacks falls back to the default version); the bash map is UTF-8.
    /// </summary>
    public static class Shims
    {
        public static string Dir { get { return Path.Combine(Paths.Bin, "shims"); } }
        static string MapDir { get { return Path.Combine(Dir, "map"); } }
        static string LibDir { get { return Path.Combine(Dir, "lib"); } }
        public static string NpmPrefixRoot { get { return Path.Combine(Paths.Usr, "npm"); } }

        public static bool Enabled { get { return Features.IsOn("shims"); } }
        public static bool NodeVersions { get { return Features.IsOn("nodeVersions"); } }

        static readonly object Sync = new object();
        static bool _hooked;

        [DllImport("kernel32.dll")] static extern uint GetOEMCP();

        static Encoding Oem
        {
            get
            {
                try { return Encoding.GetEncoding((int)GetOEMCP()); } catch { return Encoding.Default; }
            }
        }

        /// <summary>Keep the map current when the sites change (called once; cheap when the feature is off).</summary>
        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            ServiceManager.SitesChanged += () =>
            {
                var sites = ServiceManager.CurrentSites;
                Task.Run(() => Refresh(sites));
            };
        }

        /// <summary>npm global prefix for a Node folder: usr\npm\node22.</summary>
        public static string NpmPrefix(string nodeFolder)
        {
            return Path.Combine(NpmPrefixRoot, "node" + PackageTypes.ParseVersion(nodeFolder).Major);
        }

        /// <summary>Folders for PATH in front of the package folders (empty when the feature is off).</summary>
        public static List<string> PathDirs()
        {
            var list = new List<string>();
            if (!Enabled) return list;
            list.Add(Dir);
            var node = PackageTypes.ActiveFolder("nodejs");
            if (NodeVersions && node != "")
            {
                var prefix = NpmPrefix(node);
                try { Directory.CreateDirectory(prefix); } catch { }
                list.Add(prefix);
            }
            return list;
        }

        /// <summary>Write (or remove) the shim scripts and the version maps.</summary>
        public static void Refresh(List<Site> sites = null)
        {
            lock (Sync)
            {
                try
                {
                    if (!Enabled)
                    {
                        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
                        return;
                    }
                    Directory.CreateDirectory(MapDir);
                    Directory.CreateDirectory(LibDir);
                    if (sites == null) sites = Sites.Scan();
                    WriteScripts();
                    // the shim folder is the one on PATH: "portly" should work from any terminal too
                    if (Features.IsOn("cli")) Cli.EnsureCmdFile(Dir);
                    WritePhpMaps(sites);
                    WriteNodeMaps(sites);
                }
                catch (Exception ex) { Log.Warn("Shims: " + ex.Message); }
            }
        }

        // ---------------- maps ----------------
        static void WritePhpMaps(List<Site> sites)
        {
            var installed = PackageTypes.Installed("php");
            var def = PackageTypes.ActiveFolder("php");
            Write(Path.Combine(MapDir, "php-default.txt"), def, Encoding.ASCII);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in installed)   // newest first: "8" and "8.4" point to the newest build
            {
                var v = PackageTypes.ParseVersion(f);
                foreach (var k in new[] { f, v.Major + "." + v.Minor + "." + v.Build, v.Major + "." + v.Minor, v.Major.ToString(),
                    "php-" + v.Major + "." + v.Minor, v.Major + "." + v.Minor + ".*" })
                    if (!keys.ContainsKey(k)) keys[k] = f;
            }
            Write(Path.Combine(MapDir, "php-versions.txt"), Lines(keys), Encoding.ASCII);

            var map = new List<KeyValuePair<string, string>>();
            foreach (var s in sites)
            {
                var f = PhpFor(s, installed);
                if (f != null && !string.Equals(f, def, StringComparison.OrdinalIgnoreCase)) map.Add(new KeyValuePair<string, string>(s.Folder, f));
            }
            WriteSiteMaps("php", map);
        }

        /// <summary>The PHP folder a site's terminal should use: sites.ini php=, else composer.json "require.php", else null.</summary>
        public static string PhpFor(Site s, List<string> installed)
        {
            if (s.PhpOverride != "" && installed.Contains(s.PhpOverride, StringComparer.OrdinalIgnoreCase)) return s.PhpOverride;
            var c = ComposerPhp(s.Folder);
            return c == "" ? null : VersionRules.Best(installed, c);
        }

        /// <summary>composer.json "require": {"php": "..."} of a folder ("" when none).</summary>
        public static string ComposerPhp(string folder)
        {
            try
            {
                var file = Path.Combine(folder, "composer.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) return "";
                return Json.ParseObject(File.ReadAllText(file, Encoding.UTF8)).Obj("require").Str("php").Trim();
            }
            catch { return ""; }
        }

        static void WriteNodeMaps(List<Site> sites)
        {
            var installed = PackageTypes.Installed("nodejs");
            var def = PackageTypes.ActiveFolder("nodejs");
            Write(Path.Combine(MapDir, "node-default.txt"), def, Encoding.ASCII);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var majors = new StringBuilder();
            foreach (var f in installed)
            {
                var v = PackageTypes.ParseVersion(f);
                string M = v.Major.ToString(), MN = M + "." + v.Minor, MNP = MN + "." + v.Build;
                var list = new List<string> { "node", "latest", "current", "stable", "*", f,
                    M, "v" + M, M + ".x", "v" + M + ".x", M + ".*", MN, "v" + MN, MN + ".x", "v" + MN + ".x", MNP, "v" + MNP };
                if (IsLts(f)) { list.Add("lts/*"); list.Add("lts"); list.Add("lts/-0"); }
                string code;
                if (LtsNames.TryGetValue(v.Major, out code)) { list.Add("lts/" + code); list.Add(code); }
                foreach (var k in list) if (!keys.ContainsKey(k)) keys[k] = f;
                majors.Append(f).Append('|').Append("node" + v.Major).Append('\n');
            }
            Write(Path.Combine(MapDir, "node-versions.txt"), Lines(keys), Encoding.ASCII);
            // without the feature: no prefix change, no per-project lookup (the resolver checks for this file)
            var majorFile = Path.Combine(MapDir, "node-major.txt");
            if (NodeVersions) Write(majorFile, majors.ToString(), Encoding.ASCII);
            else if (File.Exists(majorFile)) File.Delete(majorFile);

            var map = new List<KeyValuePair<string, string>>();
            if (NodeVersions)
                foreach (var s in sites)
                {
                    var c = EnginesNode(s.Folder);
                    var f = c == "" ? null : VersionRules.Best(installed, c);
                    if (f != null && !string.Equals(f, def, StringComparison.OrdinalIgnoreCase)) map.Add(new KeyValuePair<string, string>(s.Folder, f));
                }
            WriteSiteMaps("node", map);
        }

        static readonly Dictionary<int, string> LtsNames = new Dictionary<int, string>
        {
            { 4, "argon" }, { 6, "boron" }, { 8, "carbon" }, { 10, "dubnium" }, { 12, "erbium" }, { 14, "fermium" },
            { 16, "gallium" }, { 18, "hydrogen" }, { 20, "iron" }, { 22, "jod" }, { 24, "krypton" },
        };

        /// <summary>LTS per the catalog tag, else the Node rule of thumb (even majors become LTS).</summary>
        static bool IsLts(string folder)
        {
            var e = Catalog.FindByFolder("nodejs", folder);
            if (e != null && e.Tag != "") return e.Tag.Equals("LTS", StringComparison.OrdinalIgnoreCase);
            return PackageTypes.ParseVersion(folder).Major % 2 == 0;
        }

        /// <summary>package.json "engines": {"node": "..."} ("" when none).</summary>
        public static string EnginesNode(string folder)
        {
            try
            {
                var file = Path.Combine(folder, "package.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) return "";
                return Json.ParseObject(File.ReadAllText(file, Encoding.UTF8)).Obj("engines").Str("node").Trim();
            }
            catch { return ""; }
        }

        /// <summary>
        /// kind-sites.txt (cmd): "length|C:\www\shop\|folder", longest first so a nested project wins over its group;
        /// kind-sites.sh.txt (bash): "C:/www/shop/|folder" in UTF-8.
        /// </summary>
        static void WriteSiteMaps(string kind, List<KeyValuePair<string, string>> map)
        {
            var oem = Oem;
            var cmd = new StringBuilder();
            var sh = new StringBuilder();
            foreach (var kv in map.OrderByDescending(x => x.Key.Length))
            {
                var prefix = kv.Key.TrimEnd('\\') + "\\";
                if (prefix.IndexOf('|') >= 0 || prefix.IndexOf('!') >= 0) continue;
                // a letter the OEM code page cannot show would never match: leave it out of the cmd map
                if (oem.GetString(oem.GetBytes(prefix)) == prefix) cmd.Append(prefix.Length).Append('|').Append(prefix).Append('|').Append(kv.Value).Append('\n');
                sh.Append(prefix.Replace('\\', '/')).Append('|').Append(kv.Value).Append('\n');
            }
            Write(Path.Combine(MapDir, kind + "-sites.txt"), cmd.ToString(), oem);
            Write(Path.Combine(MapDir, kind + "-sites.sh.txt"), sh.ToString(), new UTF8Encoding(false));
        }

        static string Lines(Dictionary<string, string> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d) sb.Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        static void Write(string file, string content, Encoding enc)
        {
            try
            {
                var bytes = enc.GetBytes(content);
                if (File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(bytes)) return;
                File.WriteAllBytes(file, bytes);
            }
            catch (Exception ex) { Log.Warn("Shims: " + file + ": " + ex.Message); }
        }

        // ---------------- scripts ----------------
        static void WriteScripts()
        {
            var oem = Oem;
            Write(Path.Combine(LibDir, "resolve-php.cmd"), Crlf(Resolver("php", "PHP", new[] { ".php-version" }, "php", "php.exe")), oem);
            Write(Path.Combine(LibDir, "resolve-node.cmd"), Crlf(Resolver("node", "Node.js", new[] { ".nvmrc", ".node-version" }, "nodejs", "node.exe")), oem);
            Write(Path.Combine(LibDir, "resolve.sh"), ResolverSh, new UTF8Encoding(false));

            var composer = PackageTypes.ActiveFolder("composer");
            var wp = PackageTypes.ActiveFolder("wpcli");
            Write(Path.Combine(Dir, "php.cmd"), Crlf(PhpCmd("\"%PORTLY_PHP_DIR%\\php.exe\" %*", null, null)), oem);
            Write(Path.Combine(Dir, "composer.cmd"), Crlf(PhpCmd("\"%PORTLY_PHP_DIR%\\php.exe\" \"%PHAR%\" %*", @"%~dp0..\composer\" + composer + @"\composer.phar", "Composer")), oem);
            Write(Path.Combine(Dir, "wp.cmd"), Crlf(PhpCmd("\"%PORTLY_PHP_DIR%\\php.exe\" \"%PHAR%\" %*", @"%~dp0..\wpcli\" + wp + @"\wp-cli.phar", "WP-CLI")), oem);
            Write(Path.Combine(Dir, "node.cmd"), Crlf(NodeCmd("\"%PORTLY_NODE_DIR%\\node.exe\" %*")), oem);
            Write(Path.Combine(Dir, "npm.cmd"), Crlf(NodeCmd("\"%PORTLY_NODE_DIR%\\node.exe\" \"%PORTLY_NODE_DIR%\\node_modules\\npm\\bin\\npm-cli.js\" %*")), oem);
            Write(Path.Combine(Dir, "npx.cmd"), Crlf(NodeCmd("\"%PORTLY_NODE_DIR%\\node.exe\" \"%PORTLY_NODE_DIR%\\node_modules\\npm\\bin\\npx-cli.js\" %*")), oem);

            var utf8 = new UTF8Encoding(false);
            Write(Path.Combine(Dir, "php"), Sh("php", "\"$PORTLY_DIR/php.exe\" \"$@\""), utf8);
            Write(Path.Combine(Dir, "composer"), Sh("php", "\"$PORTLY_DIR/php.exe\" \"$portly_shims/../composer/" + composer + "/composer.phar\" \"$@\""), utf8);
            Write(Path.Combine(Dir, "wp"), Sh("php", "\"$PORTLY_DIR/php.exe\" \"$portly_shims/../wpcli/" + wp + "/wp-cli.phar\" \"$@\""), utf8);
            Write(Path.Combine(Dir, "node"), Sh("node", "\"$PORTLY_DIR/node.exe\" \"$@\""), utf8);
            Write(Path.Combine(Dir, "npm"), Sh("node", "\"$PORTLY_DIR/node.exe\" \"$PORTLY_DIR/node_modules/npm/bin/npm-cli.js\" \"$@\""), utf8);
            Write(Path.Combine(Dir, "npx"), Sh("node", "\"$PORTLY_DIR/node.exe\" \"$PORTLY_DIR/node_modules/npm/bin/npx-cli.js\" \"$@\""), utf8);
            Write(Path.Combine(Dir, "README.txt"), Crlf(Readme), utf8);
        }

        static string Crlf(string s) { return s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        static string PhpCmd(string run, string phar, string pharName)
        {
            var sb = new StringBuilder();
            sb.Append("@echo off\n");
            sb.Append("rem Portly shim (generated, do not edit): runs the PHP version of the current project. See README.txt.\n");
            sb.Append("setlocal DisableDelayedExpansion\n");
            sb.Append("call \"%~dp0lib\\resolve-php.cmd\"\n");
            sb.Append("if not defined PORTLY_PHP_DIR exit /b 1\n");
            if (phar != null)
            {
                sb.Append("set \"PHAR=" + phar + "\"\n");
                sb.Append("if not exist \"%PHAR%\" (\n");
                sb.Append("  >&2 echo Portly: " + pharName + " is not installed. Install it in Portly: Packages ^> " + pharName + ".\n");
                sb.Append("  exit /b 1\n)\n");
            }
            // the same PHP for scripts that call "php" again (composer scripts, artisan ...)
            sb.Append("set \"PATH=%PORTLY_PHP_DIR%;%~dp0;%PATH%\"\n");
            sb.Append(run + "\n");
            sb.Append("exit /b %ERRORLEVEL%\n");
            return sb.ToString();
        }

        static string NodeCmd(string run)
        {
            return "@echo off\n"
                + "rem Portly shim (generated, do not edit): runs the Node.js version of the current project. See README.txt.\n"
                + "setlocal DisableDelayedExpansion\n"
                + "call \"%~dp0lib\\resolve-node.cmd\"\n"
                + "if not defined PORTLY_NODE_DIR exit /b 1\n"
                + "if defined PORTLY_NPM_PREFIX set \"NPM_CONFIG_PREFIX=%PORTLY_NPM_PREFIX%\"\n"
                + "if defined PORTLY_NPM_PREFIX set \"PATH=%PORTLY_NPM_PREFIX%;%PATH%\"\n"
                + "set \"PATH=%PORTLY_NODE_DIR%;%~dp0;%PATH%\"\n"
                + run + "\n"
                + "exit /b %ERRORLEVEL%\n";
        }

        /// <summary>
        /// The cmd resolver. Sets PORTLY_&lt;KIND&gt;_DIR (empty + message on stderr when nothing fits). Values read from
        /// project files are stripped of quotes and only used inside quotes, so a crafted version file cannot run commands.
        /// </summary>
        static string Resolver(string kind, string label, string[] files, string binDir, string exe)
        {
            var K = kind.ToUpperInvariant();
            var sb = new StringBuilder();
            sb.Append("@echo off\n");
            sb.Append("rem Portly shim helper (generated, do not edit): picks the " + label + " version for the current folder.\n");
            sb.Append("rem Order: " + string.Join(" / ", files) + " here or in a parent folder, Portly site settings, Portly's default.\n");
            sb.Append("setlocal DisableDelayedExpansion\n");
            sb.Append("set \"M=%~dp0..\\map\"\n");
            sb.Append("set \"F=\"\nset \"V=\"\nset \"S=\"\n");
            // per-project version files (node: only with the "nodeVersions" feature, signalled by node-major.txt)
            if (kind == "node") sb.Append("if not exist \"%M%\\node-major.txt\" goto default\n");
            sb.Append("set \"d=%CD%\"\n");
            sb.Append(":up\n");
            foreach (var f in files) sb.Append("if exist \"%d%\\" + f + "\" set \"S=" + f + "\" & goto file\n");
            sb.Append("for %%I in (\"%d%\\..\") do set \"n=%%~fI\"\n");
            sb.Append("if /i \"%n%\"==\"%d%\" goto sites\n");
            sb.Append("set \"d=%n%\"\n");
            sb.Append("goto up\n");
            sb.Append(":file\n");
            sb.Append("set /p V=<\"%d%\\%S%\"\n");
            sb.Append("if not defined V goto sites\n");
            sb.Append("set \"V=%V:\"=%\"\n");
            sb.Append("for /f \"tokens=1 delims= \t\" %%x in (\"%V%\") do set \"V=%%x\"\n");
            sb.Append("if not defined V goto sites\n");
            sb.Append("for /f \"usebackq tokens=1,* delims=|\" %%a in (\"%M%\\" + kind + "-versions.txt\") do if not defined F if /i \"%%a\"==\"%V%\" set \"F=%%b\"\n");
            sb.Append("if defined F goto found\n");
            sb.Append(">&2 echo Portly: " + label + " \"%V%\" from %S% is not installed. Install it in Portly: Packages ^> " + label + ".\n");
            sb.Append("endlocal & set \"PORTLY_" + K + "_DIR=\" & exit /b 1\n");
            sb.Append(":sites\n");
            sb.Append("if not exist \"%M%\\" + kind + "-sites.txt\" goto default\n");
            sb.Append("set \"c=%CD%\\\"\n");
            sb.Append("set \"R=\"\n");
            sb.Append("setlocal EnableDelayedExpansion\n");
            sb.Append("for /f \"usebackq tokens=1,2,* delims=|\" %%a in (\"%M%\\" + kind + "-sites.txt\") do if not defined R (\n");
            sb.Append("  set \"p=!c:~0,%%a!\"\n");
            sb.Append("  if /i \"!p!\"==\"%%b\" set \"R=%%c\"\n");
            sb.Append(")\n");
            sb.Append("endlocal & set \"F=%R%\"\n");
            sb.Append(":default\n");
            sb.Append("if not defined F if exist \"%M%\\" + kind + "-default.txt\" set /p F=<\"%M%\\" + kind + "-default.txt\"\n");
            sb.Append(":found\n");
            sb.Append("set \"D=\"\n");
            sb.Append("if defined F if exist \"%~dp0..\\..\\" + binDir + "\\%F%\\" + exe + "\" for %%I in (\"%~dp0..\\..\\" + binDir + "\\%F%\") do set \"D=%%~fI\"\n");
            sb.Append("if not defined D (\n");
            sb.Append("  >&2 echo Portly: no " + label + " version is installed. Install one in Portly: Packages ^> " + label + ".\n");
            sb.Append("  endlocal & set \"PORTLY_" + K + "_DIR=\" & exit /b 1\n");
            sb.Append(")\n");
            if (kind == "node")
            {
                sb.Append("set \"P=\"\n");
                sb.Append("if exist \"%M%\\node-major.txt\" for /f \"usebackq tokens=1,* delims=|\" %%a in (\"%M%\\node-major.txt\") do if /i \"%%a\"==\"%F%\" set \"P=%%b\"\n");
                sb.Append("if defined P for %%I in (\"%~dp0..\\..\\..\\usr\\npm\\%P%\") do set \"P=%%~fI\"\n");
                sb.Append("if defined P if not exist \"%P%\" mkdir \"%P%\" 2>nul\n");
                sb.Append("endlocal & set \"PORTLY_NODE_DIR=%D%\" & set \"PORTLY_NPM_PREFIX=%P%\" & exit /b 0\n");
            }
            else sb.Append("endlocal & set \"PORTLY_" + K + "_DIR=%D%\" & exit /b 0\n");
            return sb.ToString();
        }

        static string Sh(string kind, string run)
        {
            var files = kind == "php" ? "\".php-version\"" : "\".nvmrc\" \".node-version\"";
            var sb = new StringBuilder();
            sb.Append("#!/usr/bin/env bash\n");
            sb.Append("# Portly shim for Git Bash (generated, do not edit): same rules as the .cmd next to it. See README.txt.\n");
            sb.Append("portly_shims=$(cd \"$(dirname \"$0\")\" && pwd)\n");
            sb.Append(". \"$portly_shims/lib/resolve.sh\"\n");
            sb.Append("portly_resolve " + kind + " " + (kind == "php" ? "php php.exe" : "nodejs node.exe") + " " + files + " || exit 1\n");
            if (kind == "node") sb.Append("if [ -n \"$PORTLY_PREFIX\" ]; then export NPM_CONFIG_PREFIX=\"$PORTLY_PREFIX_WIN\"; PATH=\"$PORTLY_PREFIX:$PATH\"; fi\n");
            sb.Append("export PATH=\"$PORTLY_DIR:$portly_shims:$PATH\"\n");
            sb.Append("exec " + run + "\n");
            return sb.ToString();
        }

        const string ResolverSh = @"# Portly shim helper for Git Bash (generated, do not edit).
# portly_resolve <kind> <bin folder> <exe> <version files...>: sets PORTLY_DIR (and PORTLY_PREFIX for Node).
portly_resolve() {
  local kind=$1 bindir=$2 exe=$3; shift 3
  local map=""$portly_shims/map"" folder="""" ver="""" src="""" d=$PWD f k v w
  local label=PHP; [ ""$kind"" = node ] && label=Node.js
  PORTLY_PREFIX=""""; PORTLY_PREFIX_WIN=""""
  if [ ""$kind"" = php ] || [ -f ""$map/node-major.txt"" ]; then
    while :; do
      for f in ""$@""; do
        if [ -f ""$d/$f"" ]; then src=""$f""; break 2; fi
      done
      if [ -z ""$d"" ] || [ ""$d"" = / ]; then break; fi
      d=${d%/*}
    done
  fi
  if [ -n ""$src"" ]; then
    IFS= read -r ver < ""$d/$src"" || true
    ver=${ver//[$'\r\t \""']/}
    if [ -n ""$ver"" ]; then
      while IFS='|' read -r k v; do
        if [ ""${k,,}"" = ""${ver,,}"" ]; then folder=$v; break; fi
      done < ""$map/$kind-versions.txt""
      if [ -z ""$folder"" ]; then
        echo ""Portly: $label \""$ver\"" from $src is not installed. Install it in Portly: Packages > $label."" >&2
        return 1
      fi
    fi
  fi
  if [ -z ""$folder"" ] && [ -s ""$map/$kind-sites.sh.txt"" ]; then
    w=$(pwd -W 2>/dev/null)/
    while IFS='|' read -r k v; do
      if [[ ""${w,,}"" == ""${k,,}""* ]]; then folder=$v; break; fi
    done < ""$map/$kind-sites.sh.txt""
  fi
  if [ -z ""$folder"" ] && [ -f ""$map/$kind-default.txt"" ]; then
    IFS= read -r folder < ""$map/$kind-default.txt"" || true
  fi
  PORTLY_DIR=""$portly_shims/../$bindir/$folder""
  if [ -z ""$folder"" ] || [ ! -f ""$PORTLY_DIR/$exe"" ]; then
    echo ""Portly: no $label version is installed. Install one in Portly: Packages > $label."" >&2
    return 1
  fi
  if [ ""$kind"" = node ] && [ -f ""$map/node-major.txt"" ]; then
    while IFS='|' read -r k v; do
      if [ ""$k"" = ""$folder"" ]; then
        PORTLY_PREFIX=""$portly_shims/../../usr/npm/$v""
        mkdir -p ""$PORTLY_PREFIX""
        PORTLY_PREFIX_WIN=$(cd ""$PORTLY_PREFIX"" && pwd -W)
        break
      fi
    done < ""$map/node-major.txt""
  fi
  return 0
}
";

        const string Readme = @"Portly shims
============
This folder is put on PATH by Portly (terminal button, and the user PATH when ""Add to PATH"" is on).
php, composer, wp, node, npm and npx here pick the version for the folder you are in:

PHP:  1. a .php-version file (e.g. 8.3) in the current folder or a parent
      2. the PHP chosen for the Portly site that contains the folder
      3. composer.json ""require.php"" of a Portly site (newest installed version that fits)
      4. Portly's default PHP
Node (Settings > Features > Several Node.js versions):
      1. .nvmrc / .node-version (22, v22.11.0, 22.x, lts/*, lts/jod ...)
      2. package.json ""engines.node"" of a Portly site
      3. the active Node.js
      npm's global packages go to usr\npm\node<major>.

The files are rewritten by Portly; do not edit them.
";
    }
}
