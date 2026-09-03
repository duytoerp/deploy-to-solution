using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeployToSolution.Services
{
    /// <summary>Thin Dataverse Web API wrapper: bearer token, OData headers, paging and readable errors.</summary>
    public class DataverseClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };

        private readonly TokenService _tokens;

        public string EnvironmentUrl { get; }
        public string ApiRoot => $"{EnvironmentUrl}/api/data/v9.2/";

        public DataverseClient(string environmentUrl, TokenService tokens)
        {
            EnvironmentUrl = environmentUrl.TrimEnd('/');
            _tokens = tokens;
        }

        public async Task<JsonElement> GetAsync(string relativeUrl, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Get, relativeUrl, null, ct).ConfigureAwait(false);
            return doc == null ? default : doc.RootElement.Clone();
        }

        /// <summary>GET that follows @odata.nextLink and returns every "value" entry.</summary>
        public async Task<List<JsonElement>> GetAllAsync(string relativeUrl, CancellationToken ct)
        {
            var results = new List<JsonElement>();
            var url = relativeUrl;
            while (!string.IsNullOrEmpty(url))
            {
                using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray())
                        results.Add(item.Clone());

                url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
            return results;
        }

        public async Task PostActionAsync(string action, object payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload);
            using var _ = await SendAsync(HttpMethod.Post, action, json, ct).ConfigureAwait(false);
        }

        public async Task<string> WhoAmIAsync(CancellationToken ct)
        {
            var me = await GetAsync("WhoAmI", ct).ConfigureAwait(false);
            var userId = me.GetProperty("UserId").GetString();
            var user = await GetAsync($"systemusers({userId})?$select=fullname,domainname", ct).ConfigureAwait(false);
            var name = user.TryGetProperty("fullname", out var f) ? f.GetString() : "(unknown)";
            var upn = user.TryGetProperty("domainname", out var d) ? d.GetString() : "";
            return string.IsNullOrWhiteSpace(upn) ? name : $"{name} <{upn}>";
        }

        private async Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, string jsonBody, CancellationToken ct)
        {
            var token = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? relativeUrl
                : ApiRoot + relativeUrl.TrimStart('/');

            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
            req.Headers.TryAddWithoutValidation("OData-Version", "4.0");

            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new DataverseException((int)resp.StatusCode, ExtractError(body, resp.ReasonPhrase));

            if (string.IsNullOrWhiteSpace(body))
                return JsonDocument.Parse("{}");

            return JsonDocument.Parse(body);
        }

        private static string ExtractError(string body, string fallback)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var msg))
                    return Collapse(msg.GetString());
            }
            catch { }
            return string.IsNullOrWhiteSpace(body) ? fallback : Collapse(body);
        }

        private static string Collapse(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 500 ? s.Substring(0, 500) + "..." : s;
        }

        /// <summary>Escapes a value for an OData string literal.</summary>
        public static string Esc(string value) => (value ?? "").Replace("'", "''");
    }

    public class DataverseException : Exception
    {
        public int StatusCode { get; }
        public DataverseException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }
}
