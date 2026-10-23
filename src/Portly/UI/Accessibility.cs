using System;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

namespace Portly.UI
{
    /// <summary>
    /// Native UI Automation provider for Portly's painted controls. Without it, UIA uses its own Win32 proxy for the
    /// BUTTON window class, which reports owner-drawn buttons as an unnamed "Pane" without patterns, so screen
    /// readers cannot press them (MSAA's AccessibleRole is never asked). The control answers WM_GETOBJECT itself:
    /// Button + Invoke, CheckBox + Toggle, Group, Text or Image with the right name.
    /// </summary>
    public sealed class UiaProvider : IRawElementProviderSimple, IInvokeProvider, IToggleProvider
    {
        public enum Kind { Button, Toggle, Group, Text, Image }

        const int WM_GETOBJECT = 0x003D;
        readonly Control _c;
        readonly Kind _kind;
        readonly IntPtr _hwnd;

        UiaProvider(Control c, Kind kind) { _c = c; _kind = kind; _hwnd = c.Handle; }

        /// <summary>Call first in the control's WndProc; returns true when the message was answered.</summary>
        public static bool Handle(Control c, Kind kind, ref UiaProvider cache, ref Message m)
        {
            if (m.Msg != WM_GETOBJECT || (int)(long)m.LParam != AutomationInteropProvider.RootObjectId) return false;
            try
            {
                if (cache == null) cache = new UiaProvider(c, kind);
                m.Result = AutomationInteropProvider.ReturnRawElementProvider(c.Handle, m.WParam, m.LParam, cache);
                return true;
            }
            catch { return false; }
        }

        T Read<T>(Func<T> f)
        {
            if (_c.IsDisposed) throw new ElementNotAvailableException();
            return _c.InvokeRequired ? (T)_c.Invoke(f) : f();
        }

        public ProviderOptions ProviderOptions { get { return ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading; } }

        public IRawElementProviderSimple HostRawElementProvider { get { return AutomationInteropProvider.HostProviderFromHandle(_hwnd); } }

        public object GetPatternProvider(int patternId)
        {
            if (_kind == Kind.Button && patternId == InvokePatternIdentifiers.Pattern.Id) return this;
            if (_kind == Kind.Toggle && patternId == TogglePatternIdentifiers.Pattern.Id) return this;
            return null;
        }

        public object GetPropertyValue(int id)
        {
            if (id == AutomationElementIdentifiers.ControlTypeProperty.Id)
            {
                switch (_kind)
                {
                    case Kind.Button: return ControlType.Button.Id;
                    case Kind.Toggle: return ControlType.CheckBox.Id;
                    case Kind.Group: return ControlType.Group.Id;
                    case Kind.Image: return ControlType.Image.Id;
                    default: return ControlType.Text.Id;
                }
            }
            if (id == AutomationElementIdentifiers.NameProperty.Id) return Read(() => _c.AccessibilityObject.Name ?? "");
            if (id == AutomationElementIdentifiers.HelpTextProperty.Id) return Read(() => _c.AccessibilityObject.Description);
            if (id == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id) return _kind == Kind.Button || _kind == Kind.Toggle;
            if (id == AutomationElementIdentifiers.IsContentElementProperty.Id || id == AutomationElementIdentifiers.IsControlElementProperty.Id) return true;
            return null;
        }

        // ---------------- Invoke ----------------
        public void Invoke()
        {
            var b = _c as Button;
            if (b == null) return;
            if (!Read(() => b.Enabled && b.Visible)) throw new ElementNotEnabledException();
            // asynchronous: a click may open a modal dialog, UIA must not wait for it
            _c.BeginInvoke((Action)(() => { if (!b.IsDisposed) b.PerformClick(); }));
        }

        // ---------------- Toggle ----------------
        public ToggleState ToggleState { get { return Read(() => ((CheckBox)_c).Checked) ? ToggleState.On : ToggleState.Off; } }

        public void Toggle()
        {
            var cb = _c as CheckBox;
            if (cb == null) return;
            if (!Read(() => cb.Enabled && cb.Visible)) throw new ElementNotEnabledException();
            // the owner decides what a click does (AutoCheck is off): start / stop
            var ts = cb as ToggleSwitch;
            _c.BeginInvoke((Action)(() => { if (cb.IsDisposed) return; if (ts != null) ts.PerformToggle(); else cb.Checked = !cb.Checked; }));
        }
    }
}
