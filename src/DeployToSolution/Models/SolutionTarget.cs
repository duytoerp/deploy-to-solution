namespace DeployToSolution.Models
{
    /// <summary>A solution the component list gets pushed into (e.g. the UAT patch and the PROD patch).</summary>
    public class SolutionTarget : ObservableObject
    {
        private bool _enabled = true;
        private string _uniqueName = "";
        private string _friendlyName = "";
        private string _version = "";
        private bool _isManaged;

        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public string UniqueName { get => _uniqueName; set => Set(ref _uniqueName, value); }
        public string FriendlyName { get => _friendlyName; set => Set(ref _friendlyName, value); }
        public string Version { get => _version; set => Set(ref _version, value); }
        public bool IsManaged { get => _isManaged; set => Set(ref _isManaged, value); }

        public string Display => string.IsNullOrWhiteSpace(FriendlyName)
            ? UniqueName
            : $"{FriendlyName}  ({UniqueName})";

        public override string ToString() => Display;
    }
}
