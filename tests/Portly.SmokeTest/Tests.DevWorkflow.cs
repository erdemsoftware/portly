using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "DevWorkflow" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_DevWorkflow(Settings s, string www)
        {
            // ---------- YAML subset ----------
            var y = Yaml.ParseMap(
                "# comment\n" +
                "php: \"8.3\"\n" +
                "node: 22\n" +
                "ver: 8.10\n" +
                "root: public   # trailing comment\n" +
                "domains: [shop.test, \"*.shop.portly\"]\n" +
                "database: { name: shop, collation: utf8mb4_turkish_ci }\n" +
                "flag: true\n" +
                "jobs:\n" +
                "  - name: queue\n" +
                "    command: \"php artisan queue:work # not a comment\"\n" +
                "    autostart: true\n" +
                "  - name: vite\n" +
                "    command: npm run dev\n" +
                "hooks:\n" +
                "  after-start: 'it''s ok'\n" +
                "list:\n" +
                "- a\n" +
                "- b\n");
            var jobsY = y["jobs"] as List<object>;
            Check("yaml: scalars, flow list/map, comments", (string)y["php"] == "8.3" && (long)y["node"] == 22 && Yaml.Str(y["ver"]) == "8.10" && (string)y["root"] == "public"
                && ((List<object>)y["domains"]).Count == 2 && (string)((Dictionary<string, object>)y["database"])["collation"] == "utf8mb4_turkish_ci" && (bool)y["flag"]);
            Check("yaml: list of maps, quotes, same-indent list", jobsY != null && jobsY.Count == 2 && (string)((Dictionary<string, object>)jobsY[0])["command"] == "php artisan queue:work # not a comment"
                && (bool)((Dictionary<string, object>)jobsY[0])["autostart"] && (string)((Dictionary<string, object>)y["hooks"])["after-start"] == "it's ok" && ((List<object>)y["list"]).Count == 2);
            foreach (var bad in new[] { Tuple.Create("a: 1\nb: [1, 2\n", 2), Tuple.Create("a: 1\n\tb: 2\n", 2), Tuple.Create("a: 1\nb: &x 3\n", 2), Tuple.Create("a:\n  b: 1\n    c: 2\n", 3),
                                        Tuple.Create("a: 1\na: 2\n", 2), Tuple.Create("a: \"x\n", 1), Tuple.Create("ok: 1\njust text\n", 2), Tuple.Create("a: |\n  x\n", 1) })
            {
                int line = -1;
                try { Yaml.Parse(bad.Item1); } catch (YamlException ex) { line = ex.Line; }
                Check("yaml: rejects " + bad.Item1.Replace("\n", "\\n").Replace("\t", "\\t") + " at line " + bad.Item2, line == bad.Item2, "line " + line);
            }
            var v83 = new Version(8, 3, 14);
            Check("php constraints", ProjectFile.Satisfies(v83, "8.3") && ProjectFile.Satisfies(v83, "^8.2") && !ProjectFile.Satisfies(v83, "^7.4") && ProjectFile.Satisfies(v83, ">=8.1 <8.4")
                && !ProjectFile.Satisfies(v83, "8.4") && ProjectFile.Satisfies(v83, "8.2 || 8.3") && ProjectFile.Satisfies(v83, "8.*") && !ProjectFile.Satisfies(v83, "~8.2.0"));

            // ---------- shared-log filter and cachegrind summary ----------
            var lf = new LogFilter(e => e.Contains(@"E:\www\shop\"));
            var outF = lf.Feed("[01-Jan-2026 10:00:00 UTC] PHP Warning:  x in E:\\www\\shop\\index.php on line 3\n[01-Jan-2026 10:00:01 UTC] PHP Warning:  y in E:\\www\\other\\a.php\nStack trace:\n#0 {main}\n", true);
            Check("log filter keeps only the site's entries", outF.Contains("shop") && !outF.Contains("other"), outF.Trim());
            var cg = Path.Combine(Paths.Tmp, "cachegrind.out.test");
            File.WriteAllText(cg, "version: 1\ncreator: xdebug 3.5\ncmd: E:\\www\\shop\\index.php\npart: 1\npositions: line\n\nevents: Time_(10ns) Memory_(bytes)\n\n" +
                "fl=(1) E:\\www\\shop\\index.php\nfn=(1) slow\n5 300000 0\n\nfl=(1)\nfn=(2) {main}\n1 100000 0\ncfl=(1)\ncfn=(1)\ncalls=2 0 0\n3 300000 0\n\nsummary: 400000 0\n");
            var sum = Profiler.Parse(cg);
            var slow = sum.Functions.FirstOrDefault(f => f.Name == "slow");
            Check("cachegrind summary (inclusive / self / calls)", Math.Abs(sum.TotalMs - 4) < 0.01 && sum.Functions[0].Name == "{main}" && Math.Abs(sum.Functions[0].InclusiveMs - 4) < 0.01
                && slow != null && Math.Abs(slow.SelfMs - 3) < 0.01 && slow.Calls == 2, sum.Functions.Count + " functions, total " + sum.TotalMs);
            File.Delete(cg);

            // ---------- portly.yml apply on a temp project ----------
            var proj = Path.Combine(www, "dwproj");
            Directory.CreateDirectory(Path.Combine(proj, "public"));
            File.WriteAllText(Path.Combine(proj, "public", "index.php"), "<?php echo 'DW';");
            File.WriteAllText(Path.Combine(proj, "composer.json"), "{}");
            var marker = Path.Combine(proj, "hook-ran.txt");
            var php84 = PackageTypes.Installed("php").FirstOrDefault(f => PackageTypes.Branch(f) == "8.4");
            File.WriteAllText(Path.Combine(proj, ProjectFile.FileName),
                "php: \"" + (php84 != null ? "8.4" : "8") + "\"\n" +
                "root: public\n" +
                "domains: [dwproj.test]\n" +
                "database: { name: dw_smoke, collation: utf8mb4_turkish_ci }\n" +
                "jobs:\n  - name: echo\n    command: \"echo JOB > job-ran.txt\"\n" +
                "hooks: { after-start: \"echo x > hook-ran.txt\" }\n");
            var site = Sites.Scan().First(x => x.Name == "dwproj");
            Check("portly.yml notice shown (pending)", ProjectFile.IsPending(site));
            var spec = ProjectFile.Load(Path.Combine(proj, ProjectFile.FileName));
            var plan = ProjectFile.Plan(site, spec);
            Check("portly.yml plan lists commands separately", plan.Count(p => p.IsCommand) == 2 && plan.Any(p => !p.IsCommand), string.Join(" | ", plan.Select(p => p.Text)));
            var done = await ProjectFile.ApplyAsync(site, Path.Combine(proj, ProjectFile.FileName), spec, false, null);
            site = Sites.Scan().First(x => x.Name == "dwproj");
            Check("portly.yml: PHP version set", php84 == null || site.PhpOverride == php84, site.PhpOverride);
            Check("portly.yml: alias and root", site.Aliases.Contains("dwproj.test") && site.DocRoot.EndsWith("public"), string.Join(",", site.Aliases) + " " + site.DocRoot);
            var coll = Database.Sql("SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='dw_smoke';");
            Check("portly.yml: database with collation", coll.Output.Contains("utf8mb4_turkish_ci"), coll.Output + " / " + string.Join(" | ", done));
            Check("portly.yml: commands NOT registered without the tick", Jobs.Load("dwproj").Count == 0 && Hooks.SiteHooks("dwproj").Count == 0 && !File.Exists(marker) && !File.Exists(Path.Combine(proj, "job-ran.txt")));
            Check("portly.yml: marked applied", !ProjectFile.IsPending(site));
            await ProjectFile.ApplyAsync(site, Path.Combine(proj, ProjectFile.FileName), spec, true, null);
            Check("portly.yml: commands registered (not run) with the tick", Jobs.Load("dwproj").Count == 1 && Hooks.SiteHooks("dwproj").Count == 1 && !File.Exists(marker) && !File.Exists(Path.Combine(proj, "job-ran.txt")));
            var gen = ProjectFile.Generate(site);
            Check("Create portly.yml round trip", ProjectFile.FromText(gen).Jobs.Count == 1 && ProjectFile.FromText(gen).Domains.Contains("dwproj.test"), gen.Replace("\r\n", " | "));

            // ---------- hooks with env vars ----------
            var hr = Hooks.Run("after-start", site, "echo EV=%PORTLY_EVENT% R=%PORTLY_ROOT% S=%PORTLY_SITE%");
            Check("hook runs with PORTLY_EVENT / PORTLY_ROOT / PORTLY_SITE", hr.Ok && hr.Output.Contains("EV=after-start") && hr.Output.Contains("R=" + Paths.Root) && hr.Output.Contains("S=dwproj"), hr.Output);
            Directory.CreateDirectory(Hooks.Dir);
            File.WriteAllText(Hooks.GlobalFile("before-stop"), "@echo off\r\necho %PORTLY_EVENT%> \"" + marker + "\"\r\n");
            await Hooks.RunAsync("before-stop");
            Check("global usr\\hooks\\before-stop.cmd runs", File.Exists(marker) && File.ReadAllText(marker).Contains("before-stop"));
            File.Delete(Hooks.GlobalFile("before-stop"));
            Hooks.SetSiteHooks("dwproj", new Dictionary<string, string>());

            // ---------- background job: php -r writes to the log, exits, is started again ----------
            Features.Set("jobs", true);
            var job = new JobDef { Name = "tick", Command = "php -r \"echo 'TICK-' . PHP_VERSION . PHP_EOL;\"", Restart = true };
            var log = Jobs.LogFileFor("dwproj", "tick");
            if (File.Exists(log)) File.Delete(log);
            Jobs.Start(site, job);
            var until = DateTime.Now.AddSeconds(15);
            int ticks = 0;
            while (DateTime.Now < until)
            {
                await Task.Delay(500);
                try { ticks = File.ReadAllLines(log).Count(l => l.StartsWith("TICK-")); } catch { }
                if (ticks >= 3) break;
            }
            Check("job writes to its log and is restarted after exit", ticks >= 3, ticks + " runs");
            var text = File.Exists(log) ? File.ReadAllText(log) : "";
            Check("job uses the site's PHP version first on PATH", php84 == null || text.Contains("TICK-" + PackageTypes.Label(php84)), text.Split('\n').FirstOrDefault(l => l.StartsWith("TICK-")));
            if (Environment.GetEnvironmentVariable("PORTLY_SLOW_TESTS") == "1")
            {
                // back-off 1+2+4+8+16 s: about half a minute
                until = DateTime.Now.AddSeconds(45);
                while (DateTime.Now < until && Jobs.StateOf("dwproj", "tick") != JobState.Failed) await Task.Delay(500);
                Check("job gives up after 5 restarts in a minute", Jobs.StateOf("dwproj", "tick") == JobState.Failed);
            }
            await Jobs.StopAsync("dwproj", "tick");
            // a long-running job is stopped by Stop all's job step
            Jobs.Start(site, new JobDef { Name = "sleeper", Command = "php -r \"sleep(60);\"", Restart = true });
            await Task.Delay(1500);
            Check("long job running", Jobs.StateOf("dwproj", "sleeper") == JobState.Running);
            await Jobs.StopAllAsync();
            Check("jobs stopped", Jobs.ActiveCount("dwproj") == 0 && Jobs.StateOf("dwproj", "sleeper") == JobState.Stopped);

            // ---------- CLI: command handler and a round trip over the named pipe (no second GUI) ----------
            var st = await Cli.Handle(new[] { "status" });
            Check("cli status", st.Code == 0 && st.Output.Contains("Apache") && st.Output.Contains("running"), st.Output.Split('\n').FirstOrDefault());
            var badUse = await Cli.Handle(new[] { "use", "php", "1.0" });
            Check("cli use php with a missing version fails", badUse.Code == 1);
            var sitesOut = await Cli.Handle(new[] { "sites" });
            Check("cli sites", sitesOut.Output.Contains("dwproj"));
            Cli.InstanceKey = "PortlySmoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Cli.StartServer();
            string pipeAnswer = "";
            using (var c = new NamedPipeClientStream(".", Cli.PipeName, PipeDirection.InOut))
            {
                c.Connect(5000);
                var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
                w.WriteLine("1");
                w.WriteLine("status");
                pipeAnswer = new StreamReader(c, Encoding.UTF8).ReadToEnd();
            }
            Check("cli status over the named pipe", pipeAnswer.StartsWith("0") && pipeAnswer.Contains("Apache"), pipeAnswer.Split('\n').FirstOrDefault());
            var cmdDir = Path.Combine(Paths.Tmp, "cmdtest");
            Cli.EnsureCmdFile(cmdDir);
            Check("portly.cmd written", File.ReadAllText(Path.Combine(cmdDir, "portly.cmd")).Contains("Portly.exe\" %*"));

            // ---------- clean up ----------
            Jobs.Save("dwproj", new JobDef[0]);
            Database.Sql("DROP DATABASE IF EXISTS `dw_smoke`;");
            var ini = new Ini(Paths.SitesFile);
            ini.RemoveSection("dwproj");
            ini.Save();
            try { Directory.Delete(proj, true); } catch { }
            try { Directory.Delete(cmdDir, true); } catch { }
        }
    }
}
