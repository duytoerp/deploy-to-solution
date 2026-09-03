using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeployToSolution.Services
{
    public class DeviceCodeInfo
    {
        public string UserCode { get; set; }
        public string VerificationUri { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Raw OAuth2 against login.microsoftonline.com. Device code (no app registration required)
    /// and client credentials (for pipelines) are both supported without pulling in MSAL.
    /// </summary>
    public class TokenService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

        private readonly string _tenant;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _resource;

        private string _accessToken;
        private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
        private string _refreshToken;

        public string RefreshToken => _refreshToken;
        public bool HasToken => !string.IsNullOrEmpty(_accessToken) && _expiresAt > DateTimeOffset.UtcNow;

        public TokenService(string tenant, string clientId, string clientSecret, string environmentUrl)
        {
            _tenant = string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim();
            _clientId = clientId;
            _clientSecret = clientSecret;
            _resource = environmentUrl.TrimEnd('/');
        }

        private string TokenUrl => $"https://login.microsoftonline.com/{_tenant}/oauth2/v2.0/token";
        private string DeviceCodeUrl => $"https://login.microsoftonline.com/{_tenant}/oauth2/v2.0/devicecode";

        private string UserScope => $"{_resource}/user_impersonation offline_access openid profile";
        private string AppScope => $"{_resource}/.default";

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            if (HasToken) return _accessToken;

            if (!string.IsNullOrEmpty(_clientSecret))
            {
                await ClientCredentialsAsync(ct).ConfigureAwait(false);
                return _accessToken;
            }

            if (!string.IsNullOrEmpty(_refreshToken) && await TryRefreshAsync(ct).ConfigureAwait(false))
                return _accessToken;

            throw new InvalidOperationException("Chưa đăng nhập. Bấm Kết nối để lấy device code.");
        }

        public void SeedRefreshToken(string refreshToken) => _refreshToken = refreshToken;

        public void SignOut()
        {
            _accessToken = null;
            _refreshToken = null;
            _expiresAt = DateTimeOffset.MinValue;
        }

        public async Task ClientCredentialsAsync(CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["scope"] = AppScope
            };
            using var doc = await PostFormAsync(TokenUrl, form, ct).ConfigureAwait(false);
            ReadTokenResponse(doc.RootElement);
        }

        public async Task<bool> TryRefreshAsync(CancellationToken ct)
        {
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _clientId,
                    ["refresh_token"] = _refreshToken,
                    ["scope"] = UserScope
                };
                using var doc = await PostFormAsync(TokenUrl, form, ct).ConfigureAwait(false);
                ReadTokenResponse(doc.RootElement);
                return true;
            }
            catch
            {
                _refreshToken = null;
                return false;
            }
        }

        /// <summary>Starts device code sign-in; onCode fires as soon as the user code is known.</summary>
        public async Task DeviceCodeAsync(Action<DeviceCodeInfo> onCode, CancellationToken ct)
        {
            var start = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = UserScope
            };

            string deviceCode;
            int interval;
            DateTimeOffset deadline;

            using (var doc = await PostFormAsync(DeviceCodeUrl, start, ct).ConfigureAwait(false))
            {
                var root = doc.RootElement;
                deviceCode = root.GetProperty("device_code").GetString();
                interval = root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
                var expires = root.TryGetProperty("expires_in", out var ev) ? ev.GetInt32() : 900;
                deadline = DateTimeOffset.UtcNow.AddSeconds(expires);

                onCode?.Invoke(new DeviceCodeInfo
                {
                    UserCode = root.GetProperty("user_code").GetString(),
                    VerificationUri = root.TryGetProperty("verification_uri", out var vu)
                        ? vu.GetString()
                        : "https://microsoft.com/devicelogin",
                    Message = root.TryGetProperty("message", out var mv) ? mv.GetString() : null
                });
            }

            var poll = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _clientId,
                ["device_code"] = deviceCode
            };

            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

                using var content = new FormUrlEncodedContent(poll);
                using var resp = await Http.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);

                if (resp.IsSuccessStatusCode)
                {
                    ReadTokenResponse(doc.RootElement);
                    return;
                }

                var error = doc.RootElement.TryGetProperty("error", out var ep) ? ep.GetString() : "unknown_error";
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval += 5; continue; }

                var desc = doc.RootElement.TryGetProperty("error_description", out var dp) ? dp.GetString() : error;
                throw new InvalidOperationException($"Đăng nhập thất bại ({error}): {Trim(desc)}");
            }

            throw new TimeoutException("Device code đã hết hạn, vui lòng thử lại.");
        }

        private void ReadTokenResponse(JsonElement root)
        {
            _accessToken = root.GetProperty("access_token").GetString();
            var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            // Renew a few minutes early so a long run never dies mid-way.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 300);
            if (root.TryGetProperty("refresh_token", out var r))
                _refreshToken = r.GetString();
        }

        private static async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string message = body;
                try
                {
                    using var err = JsonDocument.Parse(body);
                    if (err.RootElement.TryGetProperty("error_description", out var d))
                        message = d.GetString();
                }
                catch { }
                throw new InvalidOperationException($"OAuth {(int)resp.StatusCode}: {Trim(message)}");
            }
            return JsonDocument.Parse(body);
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 400 ? s.Substring(0, 400) + "..." : s;
        }
    }
}
