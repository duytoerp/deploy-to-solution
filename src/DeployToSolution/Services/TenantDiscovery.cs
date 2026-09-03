using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeployToSolution.Services
{
    /// <summary>
    /// Finds the Azure AD tenant that owns a Dataverse environment, so the user only needs
    /// the environment URL. An unauthenticated call returns 401 with a WWW-Authenticate header
    /// carrying authorization_uri=https://login.microsoftonline.com/{tenantId}/oauth2/authorize.
    /// </summary>
    public static class TenantDiscovery
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly Regex TenantPattern = new Regex(
            @"login\.microsoftonline\.com/(?<tenant>[0-9a-fA-F-]{36}|[A-Za-z0-9.\-]+)/",
            RegexOptions.Compiled);

        public static async Task<string> GetTenantIdAsync(string environmentUrl, CancellationToken ct)
        {
            var url = environmentUrl.TrimEnd('/') + "/api/data/v9.2/";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);

            if (!resp.Headers.TryGetValues("WWW-Authenticate", out var values)) return null;

            foreach (var header in values)
            {
                var match = TenantPattern.Match(header ?? "");
                if (!match.Success) continue;

                var tenant = match.Groups["tenant"].Value;
                // "common" / "organizations" are placeholders, not a real tenant.
                if (tenant.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                    tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase)) continue;
                return tenant;
            }

            return null;
        }

        public static bool IsPlaceholder(string tenant)
        {
            tenant = (tenant ?? "").Trim();
            return tenant.Length == 0 ||
                   tenant.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                   tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase);
        }
    }
}
