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
    /// when needed, the CA bundle for PHP is rebuilt and the web server reloads.
    /// </summary>
    public static class SslRepair
    {
        public class SiteResult
        {
            public Site Site;
            public string Problem;      // null = fine; "missing" / "expiring" / "otherCa" / "names" / "broken" / "custom"
            public bool Renewed;
        }

        public class Result
        {
            public bool SslOff;
            public bool CaWasTrusted, CaTrusted;
            public int FirefoxProfiles;
            public List<SiteResult> Sites = new List<SiteResult>();
            public int Renewed { get { return Sites.Count(s => s.Renewed); } }
            public List<SiteResult> Remaining { get { return Sites.Where(s => s.Problem != null && !s.Renewed).ToList(); } }
        }

        /// <summary>Only checks (no changes): what "Fix SSL" would do.</summary>
        public static Result Check(List<Site> sites)
        {
            var r = new Result { SslOff = !Settings.Current.Ssl, CaWasTrusted = Certs.IsCaTrusted() };
            r.CaTrusted = r.CaWasTrusted;
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
                foreach (var sr in r.Sites.Where(x => x.Problem != null && x.Problem != "custom"))
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
            Log.Info(I18n.T("sslfix.log", r.Sites.Count, r.Renewed));
            return r;
        }

        public static string ProblemText(string problem)
        {
            return I18n.T("sslfix.problem." + problem);
        }
    }
}
