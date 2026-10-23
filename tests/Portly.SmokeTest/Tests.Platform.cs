using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Portly.Core;
using Portly.Services;
using Portly.Tools;
using Portly.UI;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "Platform" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_Platform(Settings s, string www)
        {
            await Task.Yield();
            DeployChecks();
            ServiceModeChecks();
            UiaChecks();
        }

        // ---------------- push / pull: command lines and excludes (no server needed) ----------------
        static void DeployChecks()
        {
            var p = new DeployProfile { Site = "shop", Host = "example.com", Port = 2222, User = "web1", RemoteDir = "/var/www/vhosts/example.com/httpdocs", KeyFile = @"C:\Portly Test\usr\ssh\id_ed25519_portly" };
            var args = Deploy.SshArgs(p, Deploy.RemoteCommand("echo ok"));
            Check("deploy: ssh never prompts and accepts only new host keys", args.Contains("-o BatchMode=yes") && args.Contains("StrictHostKeyChecking=accept-new"), args);
            Check("deploy: own known_hosts, key file quoted, port and target", args.Contains("UserKnownHostsFile=") && args.Contains("-i \"C:\\Portly Test\\usr\\ssh\\id_ed25519_portly\"") && args.Contains("-p 2222") && args.Contains(" web1@example.com "), args);
            p.Port = 22;
            Check("deploy: default port is not passed", !Deploy.SshArgs(p, null).Contains(" -p "));

            var script = "D='/x'\necho \"a'b\" $D\n";
            var cmd = Deploy.RemoteCommand(script);
            var b64 = cmd.Split(' ').First(x => x.Length > 10 && !x.Contains("$") && !x.Contains("\""));
            Check("deploy: remote script travels base64 encoded and unchanged", Encoding.UTF8.GetString(Convert.FromBase64String(b64)) == script && cmd.StartsWith("sh -c \"$(echo "), cmd);
            Check("deploy: shell quoting of a single quote", Deploy.Sh("a'b") == "'a'\\''b'", Deploy.Sh("a'b"));
            Check("deploy: Windows argument quoting", Deploy.Arg("a b") == "\"a b\"" && Deploy.Arg("x\"y") == "\"x\\\"y\"" && Deploy.Arg(@"C:\a b\") == "\"C:\\a b\\\\\"", Deploy.Arg(@"C:\a b\"));

            var ex = Deploy.DefaultExcludes.ToList();
            Check("deploy: remote tar excludes are quoted", Deploy.RemoteExcludes(new[] { "node_modules", "*.log" }) == "--exclude='node_modules' --exclude='*.log'", Deploy.RemoteExcludes(new[] { "node_modules", "*.log" }));
            Check("deploy: local tar excludes", Deploy.LocalExcludes(new[] { "storage/logs" }) == "--exclude storage/logs");
            Check("deploy: excluded paths", Deploy.IsExcluded(".git", ex) && Deploy.IsExcluded(@"app\node_modules\x\y.js", ex) && Deploy.IsExcluded("storage/logs/laravel.log", ex)
                && Deploy.IsExcluded(@"wp-content\cache\page.html", ex) && Deploy.IsExcluded("error.log", ex) && Deploy.IsExcluded(".env", ex) && Deploy.IsExcluded("wp-config.php", ex));
            Check("deploy: kept paths", !Deploy.IsExcluded("index.php", ex) && !Deploy.IsExcluded("storage/app/file.txt", ex) && !Deploy.IsExcluded("wp-content/uploads/a.jpg", ex) && !Deploy.IsExcluded("logs.php", ex) && !Deploy.IsExcluded(".envrc", ex));
            Check("deploy: exclude list parsing drops '..' and empty entries", string.Join("|", Deploy.SplitExcludes(" .git , ../etc,, storage\\logs/ ;*.log")) == ".git|storage/logs|*.log", string.Join("|", Deploy.SplitExcludes(" .git , ../etc,, storage\\logs/ ;*.log")));

            p.DbFromServer = false; p.DbName = "shop_live"; p.DbUser = "shop"; p.DbPassword = "s3cr\"et'pw"; p.LocalDb = "shop";
            var all = Deploy.PullDbScript(p) + Deploy.PushDbScript(p, true) + Deploy.BackupScript(p, "2026-01-01") + Deploy.PullFilesScript(p) + Deploy.PushFilesScript(p);
            Check("deploy: password never part of a remote command", !all.Contains("s3cr") && !Deploy.SshArgs(p, Deploy.RemoteCommand(Deploy.PullDbScript(p))).Contains("s3cr"));
            Check("deploy: password goes through standard input", Encoding.UTF8.GetString(Deploy.CredentialInput(p)).Contains("\ns3cr\"et'pw\n"));
            Check("deploy: option file is private and removed", all.Contains("umask 077") && all.Contains("trap 'rm -f \"$CNF\"' EXIT"));
            Check("deploy: backup before push goes to ~/portly-backups", Deploy.BackupScript(p, "2026-01-01").Contains("$HOME/portly-backups") && Deploy.BackupScript(p, "2026-01-01").Contains("shop-2026-01-01.tar.gz"));
            Check("deploy: dangerous remote folders refused", Deploy.Validate(new DeployProfile { Site = "a", Host = "h", User = "u", RemoteDir = "/", LocalDb = "a" }) != null
                && Deploy.Validate(new DeployProfile { Site = "a", Host = "h", User = "u", RemoteDir = "/etc", LocalDb = "a" }) != null
                && Deploy.Validate(new DeployProfile { Site = "a", Host = "h", User = "u", RemoteDir = "~/public_html", LocalDb = "a" }) == null);

            // syntax of the generated server scripts (Git for Windows' sh, when present)
            var sh = new[] { @"C:\Program Files\Git\bin\sh.exe", @"C:\Program Files\Git\usr\bin\sh.exe" }.FirstOrDefault(File.Exists);
            if (sh != null)
            {
                p.DbFromServer = true;
                var bad = new[] { Deploy.PullDbScript(p), Deploy.BackupScript(p, "x"), Deploy.PushDbScript(p, true), Deploy.ProbeScript(p) }
                    .Select(x => { var f = Path.Combine(Paths.Tmp, "deploy-syntax.sh"); File.WriteAllText(f, x.Replace("\r\n", "\n"), new UTF8Encoding(false)); return ProcessUtil.Run(sh, "-n \"" + f + "\"", Paths.Tmp, null, 10000); })
                    .Where(r => !r.Ok).Select(r => r.Output).ToList();
                Check("deploy: server scripts are valid sh", bad.Count == 0, string.Join(" | ", bad));
            }

            // profile file: the database password is DPAPI-protected, never plain
            var ini = Path.Combine(Paths.Tmp, "deploy-test.ini");
            try
            {
                File.Delete(ini);
                Deploy.Save(p, ini);
                var text = File.ReadAllText(ini);
                var back = Deploy.Load("shop", ini);
                Check("deploy: profile keeps the password encrypted", !text.Contains("s3cr") && text.Contains("DbPassword="), text.Length.ToString());
                Check("deploy: profile loads back", back.DbPassword == p.DbPassword && back.Host == p.Host && back.RemoteDir == p.RemoteDir && back.Excludes.SequenceEqual(p.Excludes) && !back.DbFromServer == !p.DbFromServer);
            }
            finally { try { File.Delete(ini); } catch { } }
        }

        // ---------------- Windows service mode: names and command lines (nothing is installed) ----------------
        static void ServiceModeChecks()
        {
            const string sid = "S-1-5-21-1000-2000-3000-1001";
            var cmds = ServiceMode.InstallCommands(@"C:\Portly\Portly.exe", sid, @"C:\Portly");
            Check("service: name and command line", ServiceMode.Name == "Portly" && ServiceMode.ServiceCommandLine(@"C:\Portly\Portly.exe") == "\"C:\\Portly\\Portly.exe\" --service");
            Check("service: sc create, automatic start, LocalSystem", cmds[0].StartsWith("create Portly binPath= \"\\\"C:\\Portly\\Portly.exe\\\" --service\"") && cmds[0].Contains("start= auto") && cmds[0].Contains("obj= LocalSystem"), cmds[0]);
            Check("service: restarts after a crash", cmds.Any(c => c.StartsWith("failure Portly") && c.Contains("restart/5000")));
            Check("service: the installing user may start / stop it", cmds.Any(c => c.StartsWith("sdset Portly") && c.Contains("(A;;CCLCSWRPWPDTLOCRRC;;;" + sid + ")")));
            Check("service: user keeps write access to files the service creates", ServiceMode.IcaclsArgs(@"C:\Portly", sid) == "C:\\Portly /grant *" + sid + ":(OI)(CI)M /Q", ServiceMode.IcaclsArgs(@"C:\Portly", sid));
            Check("service: pipe name is per Portly folder", ServiceMode.PipeName.StartsWith("portly-service-") && ServiceMode.PipeName.Length == "portly-service-".Length + 8);
            var states = ServiceMode.ParseStates(ServiceMode.FormatStates(ServiceManager.Rows) + "\nweb2\tError\tline one" + (char)0x2028 + "line two");
            Check("service: state report round trip", states.Count == ServiceManager.Rows.Count + 1 && states["web2"].Item1 == ServiceState.Error && states["web2"].Item2 == "line one\nline two");
            Check("service: GUI is no controller while the setting is off", !Settings.Current.RunAsService && !ServiceMode.IsController);
        }

        // ---------------- UI Automation: screen readers can press Portly's buttons ----------------
        static void UiaChecks()
        {
            int clicks = 0, toggles = 0;
            Form form = null;
            var ready = new ManualResetEvent(false);
            var ui = new Thread(() =>
            {
                form = new Form { Text = "Portly UIA test", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-2000, -2000), Size = new System.Drawing.Size(500, 200) };
                var b = Ui.Button("Start all", (o, e) => clicks++, true, "play");
                var glyphOnly = new ModernButton { Glyph = "more", Kind = ButtonKind.Ghost, AutoSize = false, Size = new System.Drawing.Size(32, 32), Left = 200 };
                var t = new ToggleSwitch { AccessibleName = "Apache", Left = 0, Top = 60 };
                t.Click += (o, e) => { toggles++; t.Checked = !t.Checked; };
                var card = new Card { Left = 260, Top = 60, Size = new System.Drawing.Size(150, 60) };
                card.Controls.Add(new Label { Text = "MySQL", AutoSize = true });
                var pill = new Pill { Text = "Running", Left = 100, Top = 60 };
                form.Controls.AddRange(new Control[] { b, glyphOnly, t, card, pill });
                form.Shown += (o, e) => ready.Set();
                Application.Run(form);
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = true;
            ui.Start();
            if (!ready.WaitOne(15000)) { Check("UIA: test window opened", false); return; }
            try
            {
                IntPtr h = IntPtr.Zero;
                form.Invoke((Action)(() => h = form.Handle));
                var root = AutomationElement.FromHandle(h);
                var btn = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Start all"));
                object pat = null;
                bool invoke = btn != null && btn.Current.ControlType == ControlType.Button && btn.TryGetCurrentPattern(InvokePattern.Pattern, out pat);
                if (invoke) { ((InvokePattern)pat).Invoke(); for (int i = 0; i < 20 && clicks == 0; i++) Thread.Sleep(100); }
                Check("UIA: ModernButton is a Button with InvokePattern and Invoke clicks it", invoke && clicks == 1, btn == null ? "not found" : btn.Current.ControlType.ProgrammaticName + " clicks=" + clicks);
                var glyph = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "more"));
                Check("UIA: glyph-only button still has a name", glyph != null && glyph.Current.ControlType == ControlType.Button);
                var tog = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Apache"));
                object tp = null;
                bool toggle = tog != null && tog.TryGetCurrentPattern(TogglePattern.Pattern, out tp);
                if (toggle) { ((TogglePattern)tp).Toggle(); for (int i = 0; i < 20 && toggles == 0; i++) Thread.Sleep(100); }
                Check("UIA: ToggleSwitch has the Toggle pattern and runs its action", toggle && toggles == 1, "toggles=" + toggles);
                var group = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
                Check("UIA: Card is a named group", group != null && group.Current.Name == "MySQL", group == null ? "none" : group.Current.Name);
                var text = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Running"));
                Check("UIA: Pill reads as text", text != null && text.Current.ControlType == ControlType.Text, text == null ? "none" : text.Current.ControlType.ProgrammaticName);
            }
            catch (Exception ex) { Check("UIA checks", false, ex.Message); }
            finally { try { form.Invoke((Action)(() => form.Close())); } catch { } }
        }
    }
}
