using System;
using System.IO;
using System.Text.Json;
using DeployToSolution.Models;

namespace DeployToSolution.Services
{
    public static class SettingsStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeployToSolution");

        private static string SettingsFile => Path.Combine(Dir, "settings.json");
        private static string TokenFile => Path.Combine(Dir, "token.dat");

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
            }
            catch { /* corrupt settings must never block startup */ }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, Opts));
            }
            catch { }
        }

        /// <summary>Refresh tokens are stored DPAPI-encrypted for the current Windows user only.</summary>
        public static void SaveRefreshToken(string key, string refreshToken)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (string.IsNullOrEmpty(refreshToken)) { ClearRefreshToken(); return; }
                var payload = JsonSerializer.Serialize(new { key, token = refreshToken });
                File.WriteAllText(TokenFile, Dpapi.Protect(payload));
            }
            catch { }
        }

        public static string LoadRefreshToken(string key)
        {
            try
            {
                if (!File.Exists(TokenFile)) return null;
                var plain = Dpapi.Unprotect(File.ReadAllText(TokenFile));
                if (string.IsNullOrEmpty(plain)) return null;
                using var doc = JsonDocument.Parse(plain);
                var storedKey = doc.RootElement.GetProperty("key").GetString();
                if (!string.Equals(storedKey, key, StringComparison.OrdinalIgnoreCase)) return null;
                return doc.RootElement.GetProperty("token").GetString();
            }
            catch { return null; }
        }

        public static void ClearRefreshToken()
        {
            try { if (File.Exists(TokenFile)) File.Delete(TokenFile); } catch { }
        }
    }
}
