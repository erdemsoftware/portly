using System.Collections.Generic;
using System.Linq;

namespace Portly.Core
{
    public class Feature
    {
        public string Id;
        /// <summary>On for new installs? Rarely used features start off and are switched on in Settings > Features.</summary>
        public bool Default;
        public string TitleKey { get { return "feature." + Id; } }
        public string HintKey { get { return "feature." + Id + ".hint"; } }
    }

    /// <summary>
    /// Optional features. Everything rarely needed starts off so the main window stays simple; the user switches it
    /// on later in Settings > Features. Code checks Features.IsOn("id") before showing menus, tabs or starting work.
    /// </summary>
    public static class Features
    {
        public static readonly List<Feature> All = new List<Feature>
        {
            // on by default: common, cheap, no background work
            new Feature { Id = "extensions",  Default = true  },  // PHP extension installer (PECL DLLs)
            new Feature { Id = "shims",       Default = true  },  // php / composer / node follow the project's version in any terminal
            new Feature { Id = "aliases",     Default = true  },  // extra domains and wildcard subdomains per site
            new Feature { Id = "projectFile", Default = true  },  // portly.yml in a project sets it up
            new Feature { Id = "logs",        Default = true  },  // per-site log viewer
            new Feature { Id = "cli",         Default = true  },  // "portly" command line
            new Feature { Id = "archive",     Default = true  },  // archive / hide projects
            // off by default: background work, extra programs or advanced use
            new Feature { Id = "jobs",        Default = false },  // background jobs: queue workers, scheduler, Vite
            new Feature { Id = "dumps",       Default = false },  // dump() server and profiling
            new Feature { Id = "nodeVersions",Default = false },  // several Node.js versions, chosen by .nvmrc
            new Feature { Id = "frankenphp",  Default = false },  // FrankenPHP runtime per site (Laravel Octane)
            new Feature { Id = "caddy",       Default = false },  // Caddy as a third web server
            new Feature { Id = "wsl",         Default = false },  // let code inside WSL reach Portly's databases
            new Feature { Id = "deploy",      Default = false },  // push / pull a site to a server over SSH
            new Feature { Id = "serviceMode", Default = false },  // run web server and database as Windows services
            new Feature { Id = "customCerts", Default = false },  // own / wildcard certificate per site
        };

        public static Feature Get(string id) { return All.FirstOrDefault(f => f.Id == id); }

        public static bool IsOn(string id)
        {
            var f = Get(id);
            return Settings.Current.Raw.GetBool("features", id, f != null && f.Default);
        }

        public static void Set(string id, bool on) { Settings.Current.Raw.Set("features", id, on); }
    }
}
