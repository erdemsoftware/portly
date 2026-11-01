using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>
    /// "Fix SSL" for all sites in one go: the local CA is trusted by Windows (and Firefox), every site certificate
    /// is checked (exists, not expiring, signed by the current CA, made for the site's current names) and renewed
    /// when needed, the CA bundle for PHP is rebuilt and the web server reloads. As a separate optional step the
    /// authority can be replaced by one with name constraints (UpgradeCaAsync).
    /// </summary>
    public static class SslRepair
    {
        public class SiteResult
        {
            public Site Site;
            public string Problem;      // null = fine; "missing" / "expiring" / "otherCa" / "names" / "broken" / "custom" / "constraint"
            public bool Renewed;
        }

        public class Result
        {
            public bool SslOff;
            public bool CaWasTrusted, CaTrusted;
            public int FirefoxProfiles;
            /// <summary>The authority has no name constraints: offer the optional upgrade.</summary>
            public bool CaUnrestricted;
            /// <summary>Authorities that were replaced but are still in the trust store.</summary>
            public int OldCas;
            public List<SiteResult> Sites = new List<SiteResult>();
            public int Renewed { get { return Sites.Count(s => s.Renewed); } }
            public List<SiteResult> Remaining { get { return Sites.Where(s => s.Problem != null && !s.Renewed).ToList(); } }
            /// <summary>Sites only a new authority can help: it may not sign their names.</summary>
            public List<SiteResult> OutsideCa { get { return Sites.Where(s => s.Problem == "constraint").ToList(); } }
        }

        public class UpgradeResult
        {
            public string Error;        // null = done
            public int Renewed;
            public bool OldRemoved;
            public string JavaError;
        }

        /// <summary>Only checks (no changes): what "Fix SSL" would do.</summary>
        public static Result Check(List<Site> sites)
        {
            var r = new Result { SslOff = !Settings.Current.Ssl, CaWasTrusted = Certs.IsCaTrusted() };
            r.CaTrusted = r.CaWasTrusted;
            r.CaUnrestricted = Certs.CaIsUnrestricted();
            r.OldCas = Certs.OldCas.Count;
            foreach (var s in sites.Where(x => x.Valid))
                r.Sites.Add(new SiteResult { Site = s, Problem = ProblemOf(s) });
            return r;
        }

        static string ProblemOf(Site s)
        {
            if (Features.IsOn("customCerts") && CustomCerts.Has(s))
                return CustomCerts.Problem(s) == null ? null : "custom";   // the user's own file: report, never replace
            return Certs.SiteProblem(s.Host, s.AllNames);
        }

        /// <summary>Repair everything that can be repaired. trustCa = allow the administrator prompt for the CA.</summary>
        public static async Task<Result> FixAsync(List<Site> sites, bool trustCa)
        {
            var r = Check(sites);
            if (r.SslOff) return r;
            await Task.Run(() =>
            {
                Certs.EnsureCa();
                foreach (var sr in r.Sites.Where(x => x.Problem != null && x.Problem != "custom" && x.Problem != "constraint"))
                {
                    try { Certs.RenewSite(sr.Site.Host, sr.Site.AllNames); sr.Renewed = true; }
                    catch (Exception ex) { Log.Warn("SSL " + sr.Site.Host + ": " + ex.Message); }
                }
            });
            if (!r.CaWasTrusted && trustCa) r.CaTrusted = await Task.Run(() => Certs.TrustCa());
            // Firefox reads the Windows store only when told to (no harm when it is already set)
            if (r.CaTrusted) r.FirefoxProfiles = FirefoxTrust.EnableAll();
            await Certs.RefreshBundleAsync();
            if (ServiceManager.Web.IsActive) await ServiceManager.ReloadWebAsync();
            // an earlier upgrade whose last step was refused
            if (trustCa && r.OldCas > 0 && await Task.Run(() => Certs.RemoveOldCas(Certs.UntrustCa))) r.OldCas = 0;
            Log.Info(I18n.T("sslfix.log", r.Sites.Count, r.Renewed));
            return r;
        }

        /// <summary>
        /// Replace the authority by a new one with name constraints (also how a name outside them gets in). Nothing is
        /// lost on the way: the old authority stays trusted until the sites run on the new one, and refusing the first
        /// administrator prompt leaves everything as it was. <paramref name="trust"/> and <paramref name="untrust"/>
        /// replace the trust-store steps (the tests must not touch the real store).
        /// </summary>
        public static async Task<UpgradeResult> UpgradeCaAsync(List<Site> sites, Func<string, bool> trust = null, Func<string, bool> untrust = null)
        {
            var u = new UpgradeResult();
            var file = await Task.Run(() => Certs.PrepareNewCa());
            if (!await Task.Run(() => (trust ?? Certs.TrustFile)(file)))
            {
                Certs.DiscardNewCa();
                u.Error = I18n.T("sslfix.upgradeRefused");
                return u;
            }
            await Task.Run(() =>
            {
                Certs.ActivateNewCa();
                foreach (var s in sites.Where(x => x.Valid && !(Features.IsOn("customCerts") && CustomCerts.Has(x))))
                {
                    try { Certs.RenewSite(s.Host, s.AllNames); u.Renewed++; }
                    catch (Exception ex) { Log.Warn("SSL " + s.Host + ": " + ex.Message); }
                }
            });
            // (Firefox reads the Windows store, TrustFile already told it to.) Java keeps its own copy of the authority: only where the user imported it
            var java = JavaTrust.FindHome();
            if (java != null && JavaTrust.IsImported(java)) u.JavaError = await Task.Run(() => JavaTrust.Import(java));
            if (ServiceManager.Web.IsActive) await ServiceManager.ReloadWebAsync();
            u.OldRemoved = await Task.Run(() => Certs.RemoveOldCas(untrust ?? Certs.UntrustCa));
            Log.Info(I18n.T("sslfix.upgraded", u.Renewed));
            return u;
        }

        public static string ProblemText(string problem)
        {
            return I18n.T("sslfix.problem." + problem);
        }
    }
}
