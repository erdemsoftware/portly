using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// macOS / Linux part of the shims: bin/shims/php, composer, wp, node, npm, npx are small POSIX "sh" scripts
    /// (mode 0755) that read the same map files as the Windows scripts. They need only sh, awk and tr.
    /// The package layout is found by looking for the program in the package folder and in its bin subfolder.
    /// </summary>
    public static partial class Shims
    {
        static void WriteScriptsUnix()
        {
            string composer = PackageTypes.ActiveFolder("composer"), wp = PackageTypes.ActiveFolder("wpcli");
            WriteScriptsUnixTo(Dir, composer, wp);
        }

        /// <summary>Write every script into a folder (the tests use a temporary one). Executable files get mode 0755.</summary>
        internal static void WriteScriptsUnixTo(string dir, string composerFolder, string wpFolder)
        {
            Directory.CreateDirectory(Path.Combine(dir, "lib"));
            var utf8 = new UTF8Encoding(false);
            foreach (var kv in UnixScripts(composerFolder, wpFolder))
            {
                var file = Path.Combine(dir, kv.Key);
                Write(file, kv.Value, utf8);
                // sourced helpers and the readme need no exec bit
                if (!kv.Key.StartsWith("lib", StringComparison.Ordinal) && kv.Key != "README.txt") MakeExecutable(file);
            }
        }

        /// <summary>chmod 0755 (the .NET Framework build never runs on Unix: nothing to do there).</summary>
        static void MakeExecutable(string file)
        {
#if !NETFRAMEWORK
            try
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex) { Log.Warn("Shims chmod " + file + ": " + ex.Message); }
#endif
        }

        /// <summary>File name (relative to bin/shims) -> content. Pure: nothing is written.</summary>
        public static Dictionary<string, string> UnixScripts(string composerFolder, string wpFolder)
        {
            var d = new Dictionary<string, string>();
            d["lib/resolve.sh"] = LF(ResolverUnix);
            d["php"] = UnixShim("php", "php", "exec \"$PORTLY_DIR/php\" \"$@\"");
            d["composer"] = UnixShim("php", "php", PharRun("composer", composerFolder, "composer.phar", "Composer"));
            d["wp"] = UnixShim("php", "php", PharRun("wpcli", wpFolder, "wp-cli.phar", "WP-CLI"));
            d["node"] = UnixShim("node", "node", "exec \"$PORTLY_DIR/node\" \"$@\"");
            d["npm"] = UnixShim("node", "node", NpmRun("npm-cli.js"));
            d["npx"] = UnixShim("node", "node", NpmRun("npx-cli.js"));
            d["README.txt"] = LF(Readme.Replace("usr\\npm\\node<major>", "usr/npm/node<major>").Replace("\r\n", "\n"));
            return d;
        }

        static string LF(string s) { return s.Replace("\r\n", "\n"); }

        /// <summary>A phar run: the folder name goes in single quotes so nothing in it can be interpreted by the shell.</summary>
        static string PharRun(string packageType, string folder, string phar, string label)
        {
            if (string.IsNullOrEmpty(folder)) folder = "-";
            return "phar=\"$portly_shims/../" + packageType + "/\"" + Elevate.ShQuote(folder) + "\"/" + phar + "\"\n"
                + "if [ ! -f \"$phar\" ]; then echo \"Portly: " + label + " is not installed. Install it in Portly: Packages > " + label + ".\" >&2; exit 1; fi\n"
                + "exec \"$PORTLY_DIR/php\" \"$phar\" \"$@\"";
        }

        static string NpmRun(string js)
        {
            return "npmjs=\"$PORTLY_BASE/lib/node_modules/npm/bin/" + js + "\"\n"
                + "[ -f \"$npmjs\" ] || npmjs=\"$PORTLY_BASE/node_modules/npm/bin/" + js + "\"\n"
                + "exec \"$PORTLY_DIR/node\" \"$npmjs\" \"$@\"";
        }

        static string UnixShim(string kind, string exe, string run)
        {
            var sb = new StringBuilder();
            sb.Append("#!/bin/sh\n");
            sb.Append("# Portly shim (generated, do not edit): runs the " + (kind == "php" ? "PHP" : "Node.js") + " version of the current project. See README.txt.\n");
            sb.Append("portly_shims=$(cd \"$(dirname \"$0\")\" && pwd)\n");
            sb.Append(". \"$portly_shims/lib/resolve.sh\"\n");
            sb.Append("portly_resolve " + kind + " " + (kind == "php" ? "php" : "nodejs") + " " + exe + " " + (kind == "php" ? ".php-version" : ".nvmrc .node-version") + " || exit 1\n");
            if (kind == "node")
                sb.Append("if [ -n \"$PORTLY_PREFIX\" ]; then NPM_CONFIG_PREFIX=$PORTLY_PREFIX; export NPM_CONFIG_PREFIX; PATH=\"$PORTLY_PREFIX/bin:$PATH\"; fi\n");
            // the same program for scripts that call it again (composer scripts, artisan ...)
            sb.Append("PATH=\"$PORTLY_DIR:$portly_shims:$PATH\"; export PATH\n");
            sb.Append(run + "\n");
            return sb.ToString();
        }

        const string ResolverUnix = @"# Portly shim helper for macOS / Linux (generated, do not edit).
# portly_resolve <kind> <bin folder> <program> <version files...>
# Sets PORTLY_DIR (folder with the program), PORTLY_BASE (the package folder) and PORTLY_PREFIX (npm prefix, Node only).
portly_resolve() {
  kind=$1; bindir=$2; exe=$3; shift 3
  map=""$portly_shims/map""; folder=""""; src=""""; d=$PWD
  label=PHP; [ ""$kind"" = node ] && label=Node.js
  PORTLY_PREFIX=""""; PORTLY_DIR=""""; PORTLY_BASE=""""
  if [ ""$kind"" = php ] || [ -f ""$map/node-major.txt"" ]; then
    while :; do
      for f in ""$@""; do
        if [ -f ""$d/$f"" ]; then src=$f; break 2; fi
      done
      if [ -z ""$d"" ] || [ ""$d"" = / ]; then break; fi
      d=${d%/*}
    done
  fi
  if [ -n ""$src"" ]; then
    ver=""""
    IFS= read -r ver < ""$d/$src"" || true
    ver=$(printf '%s' ""$ver"" | tr -d '\r\t ""')
    if [ -n ""$ver"" ]; then
      folder=$(V=$ver awk -F'|' 'tolower($1) == tolower(ENVIRON[""V""]) { print $2; exit }' ""$map/$kind-versions.txt"")
      if [ -z ""$folder"" ]; then
        echo ""Portly: $label \""$ver\"" from $src is not installed. Install it in Portly: Packages > $label."" >&2
        return 1
      fi
    fi
  fi
  if [ -z ""$folder"" ] && [ -s ""$map/$kind-sites.sh.txt"" ]; then
    folder=$(W=""$PWD/"" awk -F'|' 'index(ENVIRON[""W""], $1) == 1 { print $2; exit }' ""$map/$kind-sites.sh.txt"")
  fi
  if [ -z ""$folder"" ] && [ -f ""$map/$kind-default.txt"" ]; then
    IFS= read -r folder < ""$map/$kind-default.txt"" || true
  fi
  if [ -n ""$folder"" ]; then
    base=""$portly_shims/../$bindir/$folder""
    for c in ""$base"" ""$base/bin""; do
      if [ -x ""$c/$exe"" ]; then PORTLY_DIR=$c; PORTLY_BASE=$base; break; fi
    done
  fi
  if [ -z ""$PORTLY_DIR"" ]; then
    echo ""Portly: no $label version is installed. Install one in Portly: Packages > $label."" >&2
    return 1
  fi
  if [ ""$kind"" = node ] && [ -f ""$map/node-major.txt"" ]; then
    v=$(F=$folder awk -F'|' '$1 == ENVIRON[""F""] { print $2; exit }' ""$map/node-major.txt"")
    if [ -n ""$v"" ]; then PORTLY_PREFIX=""$portly_shims/../../usr/npm/$v""; mkdir -p ""$PORTLY_PREFIX""; fi
  fi
  return 0
}
";
    }
}
