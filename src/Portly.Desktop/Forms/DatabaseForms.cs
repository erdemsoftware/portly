using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Portly.Desktop.UI;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Database dialogs (port of src\Portly\UI\DatabaseForms.cs). Signatures are fixed: other pages call them.
    /// </summary>
    public static class DatabaseUi
    {
        public enum SwitchResult { Continue, Done, Cancelled }

        public static Task RootPassword(Window owner) { throw new NotImplementedException(); }
        public static string RootStatus() { throw new NotImplementedException(); }
        public static Task NewDatabase(Window owner, string suggested) { throw new NotImplementedException(); }
        public static Task ImportSql(Window owner) { throw new NotImplementedException(); }
        public static Task CheckAndRepair(Window owner) { throw new NotImplementedException(); }

        /// <summary>
        /// Call before activating another MySQL / MariaDB version or engine. Continue = switch as usual,
        /// Done = the databases were moved and the new version is active, Cancelled = keep the current one.
        /// </summary>
        public static Task<SwitchResult> BeforeSwitch(Window owner, string engine, string folder) { throw new NotImplementedException(); }

        public static Task WslAccessDialog(Window owner) { throw new NotImplementedException(); }
    }
}
