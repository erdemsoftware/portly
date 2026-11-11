using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Portly.Core;
using Portly.Desktop;
using Portly.Desktop.UI;

namespace Portly.UiTests
{
    /// <summary>Drives the headless window like a user: finds controls by their visible names, clicks them with real (synthetic) mouse input, waits for the work behind them.</summary>
    static partial class Program
    {
        static MainWindow Win;
        static string ShotDir;
        static int ShotNo;
        static readonly List<string> UiErrors = new List<string>();

        // ------------------------------------------------------------------ event loop

        static void Tick()
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        static void Pump(int ms = 60)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            do { Tick(); Thread.Sleep(8); } while (DateTime.UtcNow < until);
        }

        /// <summary>Waits (pumping the UI loop) until the condition holds; message boxes nobody asked for are answered on the way.</summary>
        static bool WaitFor(Func<bool> cond, int seconds, bool answerStrays = true)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                Tick();
                if (cond()) return true;
                if (answerStrays) AnswerStrayDialogs();
                if (DateTime.UtcNow > until) return false;
                Thread.Sleep(20);
            }
        }

        // ------------------------------------------------------------------ windows and dialogs

        static List<Window> OpenWindows()
        {
            var list = new List<Window>();
            Action<Window> walk = null;
            walk = w => { foreach (var o in w.OwnedWindows) { list.Add(o); walk(o); } };
            walk(Win);
            return list;
        }

        static Window Top { get { var w = OpenWindows(); return w.Count > 0 ? w[w.Count - 1] : (Window)Win; } }

        static T WaitDialog<T>(int seconds = 15) where T : Window
        {
            T found = null;
            WaitFor(() => (found = OpenWindows().OfType<T>().FirstOrDefault()) != null, seconds, false);
            if (found != null) Pump(150);
            return found;
        }

        static MessageDialog WaitMessage(int seconds = 15)
        {
            MessageDialog found = null;
            WaitFor(() => (found = OpenWindows().OfType<MessageDialog>().LastOrDefault()) != null, seconds, false);
            if (found != null) Pump(150);
            return found;
        }

        static string TextOf(Visual root)
        {
            return string.Join(" | ", root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));
        }

        /// <summary>A message box nobody asked for: the CA trust question is declined, anything else is a failure and is closed.</summary>
        static void AnswerStrayDialogs()
        {
            foreach (var m in OpenWindows().OfType<MessageDialog>().ToList())
            {
                var text = TextOf(m);
                var no = FindButton(m, I18n.T("common.no"));
                if (text.Contains(Head(I18n.T("ssl.ask"))) && no != null) { Note("declined the CA trust question"); Click(no); continue; }
                if (text.Contains(Head(I18n.T("vc.ask"))) && no != null) { Note("declined the Visual C++ runtime question"); Click(no); continue; }
                UiErrors.Add("unexpected message box: " + text);
                Shot("unexpected-message", m);
                var b = no ?? FindButton(m, I18n.T("common.ok"));
                if (b != null) Click(b); else m.Close();
            }
        }

        static string Head(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Split(new[] { "\n\n" }, 2, StringSplitOptions.None)[0].Trim();
        }

        static void CloseDialog(Window w)
        {
            if (w == null) return;
            PressEscape(w);
            if (OpenWindows().Contains(w)) { w.Close(); Pump(120); }
        }

        static void PressEscape(Window w)
        {
            w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump(120);
        }

        static void CloseAllDialogs()
        {
            for (int i = 0; i < 8; i++)
            {
                var open = OpenWindows();
                if (open.Count == 0) return;
                try { open[open.Count - 1].Close(); } catch { }
                Pump(120);
            }
        }

        // ------------------------------------------------------------------ finding controls

        static string NameOf(StyledElement c) { return c is Control ctl ? AutomationProperties.GetName(ctl) : null; }

        static IEnumerable<T> All<T>(Visual root) where T : Visual { return root.GetVisualDescendants().OfType<T>(); }

        static Button FindButton(Visual root, string name)
        {
            return All<Button>(root).FirstOrDefault(b => NameOf(b) == name && b.IsEffectivelyVisible);
        }

        static Button NeedButton(Visual root, string name)
        {
            var b = FindButton(root, name);
            if (b == null)
                throw new Exception("button \"" + name + "\" not found (have: " + string.Join(", ", All<Button>(root).Where(x => x.IsEffectivelyVisible).Select(x => NameOf(x)).Where(x => !string.IsNullOrEmpty(x)).Distinct()) + ")");
            return b;
        }

        static CheckBox FindCheck(Visual root, string text)
        {
            return All<CheckBox>(root).FirstOrDefault(c => (c.Content as string) == text || (c.Content as TextBlock)?.Text == text);
        }

        static ToggleSwitch FindToggle(Visual root, string name)
        {
            return All<ToggleSwitch>(root).FirstOrDefault(t => NameOf(t) == name);
        }

        /// <summary>The window / overlay that holds the content of an open flyout (Avalonia keeps it internal).</summary>
        static Visual FlyoutRoot(FlyoutBase f)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var popup = typeof(PopupFlyoutBase).GetProperty("Popup", all)?.GetValue(f) as Popup;
            return popup == null ? null : typeof(Popup).GetProperty("Host", all)?.GetValue(popup) as Visual;
        }

        /// <summary>The flyout the last click opened (a button with a Flyout).</summary>
        static FlyoutBase OpenFlyout;

        static MenuItem FindMenuItem(string header)
        {
            var root = OpenFlyout == null ? null : FlyoutRoot(OpenFlyout);
            Func<MenuItem, bool> match = m => (m.Header as string) == header && m.IsEffectivelyVisible && m.Bounds.Width > 0;
            var mi = root == null ? null : All<MenuItem>(root).FirstOrDefault(match);
            // a menu opened by code (the sidebar's More menu) sits in the overlay layer of the window or of the dialog
            if (mi == null) foreach (var w in new Window[] { Win }.Concat(OpenWindows())) { mi = All<MenuItem>(w).FirstOrDefault(match); if (mi != null) break; }
            return mi;
        }

        // ------------------------------------------------------------------ input

        /// <summary>A real mouse click in the window of the control (the hit test decides what receives it).</summary>
        static void Click(Visual c)
        {
            var ctl = c as Control;
            if (ctl != null && !ctl.IsEffectivelyEnabled) throw new Exception("control is disabled: " + (NameOf(ctl) ?? ctl.GetType().Name));
            if (ctl != null) ctl.BringIntoView();
            Pump(40);
            var top = TopLevel.GetTopLevel(c);
            if (top == null) throw new Exception("control is not in a window");
            var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top);
            if (p == null) throw new Exception("control has no position");
            if (c is Button btn && btn.Flyout != null) OpenFlyout = btn.Flyout;
            top.MouseDown(p.Value, MouseButton.Left);
            top.MouseUp(p.Value, MouseButton.Left);
            Pump(120);
        }

        /// <summary>Closes open flyouts (a click outside a popup only closes it and is not delivered).</summary>
        static void DismissPopups()
        {
            if (OpenFlyout != null && OpenFlyout.IsOpen) OpenFlyout.Hide();
            OpenFlyout = null;
            Pump(60);
        }

        static void ClickMenuItem(string header)
        {
            MenuItem mi = null;
            WaitFor(() => (mi = FindMenuItem(header)) != null, 5, false);
            if (mi == null) throw new Exception("menu item \"" + header + "\" not found");
            Click(mi);
        }

        static void Type(TextBox box, string text)
        {
            box.Focus();
            Pump(40);
            box.SelectAll();
            var top = TopLevel.GetTopLevel(box);
            top.KeyTextInput(text);
            Pump(60);
        }

        // ------------------------------------------------------------------ screenshots

        static void Shot(string name, Window w = null)
        {
            try
            {
                w = w ?? Top;
                Pump(120);
                var frame = w.CaptureRenderedFrame();
                if (frame == null) return;
                Directory.CreateDirectory(ShotDir);
                var file = Path.Combine(ShotDir, (++ShotNo).ToString("00") + "-" + name + ".png");
                frame.Save(file, new PngBitmapEncoderOptions());
            }
            catch (Exception ex) { Console.WriteLine("  (screenshot " + name + " failed: " + ex.Message + ")"); }
        }
    }
}
