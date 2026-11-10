using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VendorGrid.Providers;
using VendorGrid.Settings;

namespace VendorGrid.Core
{
    public sealed class SearchService
    {
        const int VendorTimeoutSeconds = 25;

        /// <summary>One provider per enabled vendor, favourites first.</summary>
        public IList<IVendorProvider> BuildProviders(AppSettings s)
        {
            var list = new List<IVendorProvider>();
            foreach (var v in VendorCatalog.Ordered(s).Where(v => v.Enabled))
            {
                switch (v.Kind)
                {
                    case VendorKinds.Mouser: list.Add(new MouserProvider(s, v)); break;
                    case VendorKinds.DigiKey: list.Add(new DigiKeyProvider(s, v)); break;
                    case VendorKinds.Newark: list.Add(new NewarkProvider(s, v)); break;
                    case VendorKinds.Json: list.Add(new CustomJsonProvider(s, v)); break;
                    default: list.Add(new LinkOnlyProvider(s, v)); break;
                }
            }
            return list;
        }

        /// <summary>Queries all providers in parallel; reports each vendor as soon as it finishes. Runs on a worker thread.</summary>
        public Task RunAsync(string partNumber, IList<IVendorProvider> providers, CancellationToken ct, Action<VendorOutcome> report)
        {
            return Task.WhenAll(providers.Select(p => RunOne(p, partNumber, ct, report)));
        }

        static async Task RunOne(IVendorProvider p, string pn, CancellationToken ct, Action<VendorOutcome> report)
        {
            var url = SafeUrl(p, pn);

            if (p.IsLinkOnly)
            {
                report(new VendorOutcome { VendorId = p.Id, Vendor = p.Name, State = VendorState.LinkOnly, SearchUrl = url, Message = Loc.T("No data feed. Open the vendor's search page.") });
                return;
            }

            string reason;
            if (!p.IsConfigured(out reason))
            {
                report(new VendorOutcome { VendorId = p.Id, Vendor = p.Name, State = VendorState.NotConfigured, Message = reason, SearchUrl = url });
                return;
            }

            report(new VendorOutcome { VendorId = p.Id, Vendor = p.Name, State = VendorState.Searching, SearchUrl = url });
            var clock = Stopwatch.StartNew();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(VendorTimeoutSeconds));
                    var matches = await p.SearchAsync(pn, timeout.Token).ConfigureAwait(false);
                    foreach (var m in matches)
                    {
                        m.VendorId = p.Id;
                        m.Vendor = p.Name;
                        if (string.IsNullOrEmpty(m.Url)) m.Url = url;
                    }
                    report(new VendorOutcome
                    {
                        VendorId = p.Id, Vendor = p.Name, SearchUrl = url, LatencyMs = clock.ElapsedMilliseconds,
                        Matches = matches,
                        State = matches.Count > 0 ? VendorState.Found : VendorState.NotFound,
                        Message = matches.Count > 0 ? null : Loc.T("No listing for this part number.")
                    });
                }
            }
            catch (OperationCanceledException)
            {
                report(new VendorOutcome
                {
                    VendorId = p.Id, Vendor = p.Name, SearchUrl = url, LatencyMs = clock.ElapsedMilliseconds,
                    State = ct.IsCancellationRequested ? VendorState.Idle : VendorState.Error,
                    Message = ct.IsCancellationRequested ? Loc.T("Cancelled.") : Loc.F("Timed out after {0} s.", VendorTimeoutSeconds)
                });
            }
            catch (VendorException ex)
            {
                report(new VendorOutcome { VendorId = p.Id, Vendor = p.Name, SearchUrl = url, LatencyMs = clock.ElapsedMilliseconds, State = VendorState.Error, Message = ex.Message });
            }
            catch (Exception ex)
            {
                Log.Error(p.Name + " search failed", ex);
                report(new VendorOutcome { VendorId = p.Id, Vendor = p.Name, SearchUrl = url, LatencyMs = clock.ElapsedMilliseconds, State = VendorState.Error, Message = Loc.F("Unexpected error: {0}", ex.Message) });
            }
        }

        static string SafeUrl(IVendorProvider p, string pn)
        {
            try { return p.BuildSearchUrl(pn); } catch (Exception) { return null; }
        }
    }
}
