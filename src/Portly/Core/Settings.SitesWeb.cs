using System;
using System.Collections.Generic;
using System.Linq;

namespace Portly.Core
{
    public partial class Settings
    {
        public const string DefaultExcludedFolders = "node_modules, vendor, .git, _*, backup*, *.bak";

        /// <summary>Folder names / patterns (* and ?) that never become automatic sites. Comma separated.</summary>
        public string ExcludedFolders
        {
            get { return _ini.Get("sites", "ExcludedFolders", DefaultExcludedFolders); }
            set { _ini.Set("sites", "ExcludedFolders", value ?? ""); }
        }

        public List<string> ExcludedFolderPatterns
        {
            get
            {
                return ExcludedFolders.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }
}
