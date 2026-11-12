using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VendorGrid.Core
{
    public static class Http
    {
        public static readonly HttpClient Client = Create();

        static HttpClient Create()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VendorGrid/2.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        /// <summary>Sends the request and returns the parsed JSON body; throws VendorException on HTTP errors.</summary>
        public static async Task<object> SendJsonAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using (var response = await Client.SendAsync(request, ct).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new VendorException(Describe(response.StatusCode, body));
                try { return Json.Parse(body); }
                catch (Exception) { throw new VendorException(Loc.T("The site returned a response that is not JSON.")); }
            }
        }

        static string Describe(HttpStatusCode code, string body)
        {
            int n = (int)code;
            if (n == 401 || n == 403) return Loc.F("Authentication failed (HTTP {0}). Check the API credentials in Settings.", n);
            if (n == 429) return Loc.T("Rate limit reached (HTTP 429). Try again shortly.");
            if (n >= 500) return Loc.F("The vendor service is unavailable (HTTP {0}).", n);
            var snippet = string.IsNullOrWhiteSpace(body) ? "" : " " + (body.Length > 160 ? body.Substring(0, 160) + "…" : body);
            return Loc.F("Request failed (HTTP {0}).", n) + snippet;
        }
    }
}
