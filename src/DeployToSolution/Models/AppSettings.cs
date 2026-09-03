using System.Collections.Generic;

namespace DeployToSolution.Models
{
    public class AppSettings
    {
        public List<string> RecentEnvironments { get; set; } = new List<string>();
        public string EnvironmentUrl { get; set; } = "";
        public string Tenant { get; set; } = "";   // để trống = tự dò từ URL môi trường
        public string ClientId { get; set; } = AuthDefaults.PublicClientId;
        public string AuthMode { get; set; } = "DeviceCode";
        public bool RememberSignIn { get; set; } = true;
        public bool AddRequiredComponents { get; set; }
        public bool CleanupArchival { get; set; } = true;
        public string LastCsvPath { get; set; } = "";
        public List<string> LastTargets { get; set; } = new List<string>();
    }

    public static class AuthDefaults
    {
        /// <summary>Well-known public client used by the Power Platform tooling; no app registration needed.</summary>
        public const string PublicClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    }
}
