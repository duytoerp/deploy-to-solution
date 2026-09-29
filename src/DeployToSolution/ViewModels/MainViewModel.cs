using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeployToSolution.Models;
using DeployToSolution.Services;
using Microsoft.Win32;

namespace DeployToSolution.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly AppSettings _settings;

        private TokenService _tokens;
        private DataverseClient _client;
        private SolutionService _solutions;
        private ComponentCatalog _catalog;
        private CancellationTokenSource _cts;

        public MainViewModel()
        {
            _settings = SettingsStore.Load();

            EnvironmentUrl = _settings.EnvironmentUrl;
            Tenant = _settings.Tenant;
            ClientId = string.IsNullOrWhiteSpace(_settings.ClientId) ? AuthDefaults.PublicClientId : _settings.ClientId;
            UseClientSecret = _settings.AuthMode == "ClientSecret";
            RememberSignIn = _settings.RememberSignIn;
            AddRequiredComponents = _settings.AddRequiredComponents;
            CleanupArchival = _settings.CleanupArchival;

            foreach (var e in _settings.RecentEnvironments) RecentEnvironments.Add(e);

            ConnectCommand = new RelayCommand(async _ => await ConnectAsync(), _ => !IsBusy);
            DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected && !IsBusy);
            RefreshSolutionsCommand = new RelayCommand(async _ => await LoadSolutionsAsync(), _ => IsConnected && !IsBusy);
            ImportCsvCommand = new RelayCommand(_ => ImportList(), _ => !IsBusy);
            DownloadTemplateCommand = new RelayCommand(_ => DownloadTemplate(), _ => !IsBusy);
            PasteCommand = new RelayCommand(_ => PasteFromClipboard(), _ => !IsBusy);
            ExportCsvCommand = new RelayCommand(_ => ExportList(), _ => Rows.Count > 0);
            AddRowCommand = new RelayCommand(_ => AddRow(), _ => !IsBusy);
            ClearRowsCommand = new RelayCommand(_ => ClearRows(), _ => Rows.Count > 0 && !IsBusy);
            ResolveCommand = new RelayCommand(async _ => await ResolveAsync(), _ => IsConnected && !IsBusy && Rows.Count > 0);
            RunCommand = new RelayCommand(async _ => await RunAsync(false), _ => IsConnected && !IsBusy && Rows.Count > 0);
            DryRunCommand = new RelayCommand(async _ => await RunAsync(true), _ => IsConnected && !IsBusy && Rows.Count > 0);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
            SaveReportCommand = new RelayCommand(_ => SaveReport(), _ => Rows.Count > 0);
            DeployReportCommand = new RelayCommand(_ => ShowDeployReport(), _ => Rows.Count > 0);
            CopyLogCommand = new RelayCommand(_ => CopyLog(), _ => Log.Count > 0);
            OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
            OpenVerificationCommand = new RelayCommand(_ => OpenBrowser(VerificationUri));
            SelectAllSolutionsCommand = new RelayCommand(_ => SetAllSolutions(true), _ => !IsBusy);
            SelectNoSolutionsCommand = new RelayCommand(_ => SetAllSolutions(false), _ => !IsBusy);
            UnselectSolutionCommand = new RelayCommand(p => { if (p is SolutionTarget s) s.Enabled = false; }, _ => !IsBusy);

            NewProjectCommand = new RelayCommand(_ => NewProject(), _ => !IsBusy);
            SaveProjectCommand = new RelayCommand(_ => SaveProject(), _ => !IsBusy && Rows.Count > 0);
            SaveProjectAsCommand = new RelayCommand(_ => SaveProjectAs(), _ => !IsBusy && Rows.Count > 0);
            RenameProjectCommand = new RelayCommand(_ => RenameProject(), _ => !IsBusy && CurrentProject != null);
            DeleteProjectCommand = new RelayCommand(_ => DeleteProject(), _ => !IsBusy && CurrentProject != null);
            OpenProjectFolderCommand = new RelayCommand(_ => OpenFolder(ProjectStore.Dir));

            // Mọi thay đổi trong danh sách component đều làm dự án "bẩn" -> hiện dấu * để khỏi mất công.
            Rows.CollectionChanged += Rows_CollectionChanged;

            LoadProjects();
            Info("Sẵn sàng. Nhập URL môi trường rồi bấm Kết nối.");

            var last = _settings.LastProject;
            if (!string.IsNullOrWhiteSpace(last))
            {
                var project = Projects.FirstOrDefault(p => string.Equals(p.Name, last, StringComparison.OrdinalIgnoreCase));
                if (project != null) OpenProject(project);
            }
        }

        // ---------- state ----------

        public ObservableCollection<ComponentRow> Rows { get; } = new ObservableCollection<ComponentRow>();
        public ObservableCollection<SolutionTarget> Solutions { get; } = new ObservableCollection<SolutionTarget>();

        /// <summary>
        /// Các solution đang tick, hiện thành một khối riêng. Danh sách gốc dài hàng chục dòng nên
        /// solution đã chọn dễ bị cuộn hoặc bị ô lọc làm khuất - add nhầm môi trường là chuyện lớn.
        /// </summary>
        public ObservableCollection<SolutionTarget> ChosenSolutions { get; } = new ObservableCollection<SolutionTarget>();

        public bool HasChosen => ChosenSolutions.Count > 0;

        public string ChosenHeader => $"Đang add vào {ChosenSolutions.Count} solution";

        public ObservableCollection<string> TypeNames { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> RecentEnvironments { get; } = new ObservableCollection<string>();
        public ObservableCollection<LogLine> Log { get; } = new ObservableCollection<LogLine>();

        private string _environmentUrl = "";
        public string EnvironmentUrl { get => _environmentUrl; set => Set(ref _environmentUrl, value); }

        private string _tenant = "";
        public string Tenant { get => _tenant; set => Set(ref _tenant, value); }

        private string _clientId = AuthDefaults.PublicClientId;
        public string ClientId { get => _clientId; set => Set(ref _clientId, value); }

        private string _clientSecret = "";
        public string ClientSecret { get => _clientSecret; set => Set(ref _clientSecret, value); }

        private bool _useClientSecret;
        public bool UseClientSecret { get => _useClientSecret; set => Set(ref _useClientSecret, value); }

        private bool _rememberSignIn = true;
        public bool RememberSignIn { get => _rememberSignIn; set => Set(ref _rememberSignIn, value); }

        private bool _addRequiredComponents;
        public bool AddRequiredComponents { get => _addRequiredComponents; set { if (Set(ref _addRequiredComponents, value)) MarkProjectDirty(); } }

        private bool _cleanupArchival = true;
        public bool CleanupArchival { get => _cleanupArchival; set { if (Set(ref _cleanupArchival, value)) MarkProjectDirty(); } }

        private bool _isConnected;
        public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
        }

        public bool IsIdle => !IsBusy;

        private string _connectedAs = "";
        public string ConnectedAs { get => _connectedAs; set => Set(ref _connectedAs, value); }

        private string _status = "Chưa kết nối";
        public string Status { get => _status; set => Set(ref _status, value); }

        private int _progress;
        public int Progress { get => _progress; set => Set(ref _progress, value); }

        private int _progressMax = 1;
        public int ProgressMax { get => _progressMax; set => Set(ref _progressMax, value); }

        private string _deviceCode;
        public string DeviceCode
        {
            get => _deviceCode;
            set { if (Set(ref _deviceCode, value)) Raise(nameof(HasDeviceCode)); }
        }

        public bool HasDeviceCode => !string.IsNullOrEmpty(DeviceCode);

        private string _verificationUri;
        public string VerificationUri { get => _verificationUri; set => Set(ref _verificationUri, value); }

        private string _solutionFilter = "";
        public string SolutionFilter
        {
            get => _solutionFilter;
            set { if (Set(ref _solutionFilter, value)) ApplySolutionFilter(); }
        }

        private List<SolutionTarget> _allSolutions = new List<SolutionTarget>();
        private string _solutionsEnvUrl;   // môi trường mà _allSolutions được đọc ra
        private string _resolvedEnvUrl;    // môi trường mà GUID trên các dòng được tra ra

        // ---------- commands ----------

        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }
        public RelayCommand RefreshSolutionsCommand { get; }
        public RelayCommand ImportCsvCommand { get; }
        public RelayCommand DownloadTemplateCommand { get; }
        public RelayCommand PasteCommand { get; }
        public RelayCommand ExportCsvCommand { get; }
        public RelayCommand AddRowCommand { get; }
        public RelayCommand ClearRowsCommand { get; }
        public RelayCommand ResolveCommand { get; }
        public RelayCommand RunCommand { get; }
        public RelayCommand DryRunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand SaveReportCommand { get; }
        public RelayCommand DeployReportCommand { get; }
        public RelayCommand CopyLogCommand { get; }
        public RelayCommand OpenLogFolderCommand { get; }
        public RelayCommand OpenVerificationCommand { get; }
        public RelayCommand SelectAllSolutionsCommand { get; }
        public RelayCommand SelectNoSolutionsCommand { get; }
        public RelayCommand UnselectSolutionCommand { get; }
        public RelayCommand NewProjectCommand { get; }
        public RelayCommand SaveProjectCommand { get; }
        public RelayCommand SaveProjectAsCommand { get; }
        public RelayCommand RenameProjectCommand { get; }
        public RelayCommand DeleteProjectCommand { get; }
        public RelayCommand OpenProjectFolderCommand { get; }

        // ---------- connect ----------

        private async Task ConnectAsync()
        {
            var url = NormalizeUrl(EnvironmentUrl);
            if (url == null)
            {
                Error("URL môi trường không hợp lệ. Ví dụ: https://vus-dev.crm5.dynamics.com");
                return;
            }
            EnvironmentUrl = url;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            try
            {
                var secret = UseClientSecret ? ClientSecret : null;
                if (UseClientSecret && string.IsNullOrWhiteSpace(secret))
                {
                    Error("Chế độ Client secret cần nhập Client secret.");
                    return;
                }

                var tenant = (Tenant ?? "").Trim();
                if (TenantDiscovery.IsPlaceholder(tenant))
                {
                    Status = "Đang dò tenant từ URL môi trường...";
                    var discovered = await TenantDiscovery.GetTenantIdAsync(url, _cts.Token);
                    if (!string.IsNullOrEmpty(discovered))
                    {
                        tenant = discovered;
                        Tenant = discovered;
                        Info($"Tenant tự dò từ URL: {discovered}");
                    }
                    else if (UseClientSecret)
                    {
                        Error("Không dò được Tenant ID từ URL này. Kiểm tra lại URL môi trường, " +
                              "hoặc điền thẳng Tenant ID (GUID) vào ô Tenant.");
                        return;
                    }
                    else
                    {
                        tenant = "organizations";
                    }
                }

                _tokens = new TokenService(tenant, ClientId, secret, url);

                if (!UseClientSecret)
                {
                    var cacheKey = $"{tenant}|{ClientId}|{url}";
                    var cached = RememberSignIn ? SettingsStore.LoadRefreshToken(cacheKey) : null;

                    // Mỗi lần làm mới, Azure AD cấp refresh token mới - lưu lại để lần sau mở app
                    // không phải dùng một token đã cũ tới cả tháng.
                    _tokens.RefreshTokenChanged += token =>
                    {
                        if (RememberSignIn) SettingsStore.SaveRefreshToken(cacheKey, token);
                    };
                    var signedIn = false;

                    if (!string.IsNullOrEmpty(cached))
                    {
                        _tokens.SeedRefreshToken(cached);
                        Status = "Đang dùng lại phiên đăng nhập...";
                        signedIn = await _tokens.TryRefreshAsync(_cts.Token);
                        if (signedIn) Info("Dùng lại phiên đăng nhập đã lưu.");
                    }

                    if (!signedIn)
                    {
                        Status = "Đang chờ đăng nhập...";
                        await _tokens.DeviceCodeAsync(OnDeviceCode, _cts.Token);
                    }

                    DeviceCode = null;
                    if (RememberSignIn) SettingsStore.SaveRefreshToken(cacheKey, _tokens.RefreshToken);
                    else SettingsStore.ClearRefreshToken();
                }
                else
                {
                    Status = "Đang lấy token (client credentials)...";
                    await _tokens.ClientCredentialsAsync(_cts.Token);
                }

                _client = new DataverseClient(url, _tokens);
                _solutions = new SolutionService(_client);
                _catalog = new ComponentCatalog(_client);

                Status = "Đang kiểm tra kết nối...";
                try
                {
                    ConnectedAs = await _client.WhoAmIAsync(_cts.Token);
                }
                catch (DataverseException dex) when (dex.StatusCode == 401 || dex.StatusCode == 403)
                {
                    throw new InvalidOperationException(UseClientSecret
                        ? "Lấy được token nhưng Dataverse từ chối. Kiểm tra trong môi trường đã có " +
                          "Application user cho Client id này chưa (Power Platform admin center > Settings > " +
                          "Users + permissions > Application users), và đã gán security role " +
                          "System Customizer / System Administrator chưa. Chi tiết: " + dex.Message
                        : "Tài khoản không có quyền vào môi trường này. Chi tiết: " + dex.Message);
                }

                Status = "Đang đọc danh sách component type...";
                await _catalog.LoadAsync(_cts.Token);
                TypeNames.Clear();
                foreach (var n in _catalog.TypeNames) TypeNames.Add(n);

                // GUID tìm được ở môi trường trước không dùng được ở môi trường này (bước Kiểm tra tên
                // bỏ qua dòng đã có GUID) - xoá để lần chạy tới tra lại từ đầu.
                if (!string.Equals(_resolvedEnvUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    var stale = Rows.Count(r => r.IsResolved);
                    foreach (var row in Rows) row.ClearResolution();
                    if (stale > 0 && _resolvedEnvUrl != null)
                        Warn($"Đổi môi trường từ {_resolvedEnvUrl}: xoá kết quả tìm tên cũ của {stale} dòng, sẽ tra lại khi chạy.");
                }
                _resolvedEnvUrl = url;

                _client.Notice += Warn;

                IsConnected = true;
                Good($"Đã kết nối {url} với {ConnectedAs}. {_catalog.Types.Count} component type khả dụng.");

                RememberEnvironment(url);
                SaveSettings();

                await LoadSolutionsAsync();
                Status = "Đã kết nối";
            }
            catch (OperationCanceledException)
            {
                Warn("Đã hủy đăng nhập.");
                Status = "Đã hủy";
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Error(ex.Message);
                Status = "Kết nối thất bại";
            }
            finally
            {
                DeviceCode = null;
                IsBusy = false;
            }
        }

        private void OnDeviceCode(DeviceCodeInfo info)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                DeviceCode = info.UserCode;
                VerificationUri = info.VerificationUri;
                Step($"Mở {info.VerificationUri} và nhập mã: {info.UserCode}  (mã đã được copy vào clipboard)");
                try { Clipboard.SetText(info.UserCode); } catch { }
                OpenBrowser(info.VerificationUri);
            });
        }

        private void Disconnect()
        {
            _tokens?.SignOut();
            SettingsStore.ClearRefreshToken();
            IsConnected = false;
            ConnectedAs = "";
            Solutions.Clear();
            foreach (var s in _allSolutions) s.PropertyChanged -= Solution_PropertyChanged;
            _allSolutions.Clear();
            _solutionsEnvUrl = null;
            RefreshChosen();
            Status = "Đã đăng xuất";
            Info("Đã đăng xuất và xóa phiên đăng nhập đã lưu.");
        }

        private async Task LoadSolutionsAsync()
        {
            if (_solutions == null) return;
            try
            {
                Status = "Đang tải danh sách solution...";
                if (_cts == null || _cts.IsCancellationRequested) _cts = new CancellationTokenSource();

                // Dự án đang mở quyết định solution đích (kèm những gì vừa tick/bỏ tick trên màn hình).
                // Không có dự án: bấm Làm mới thì giữ đúng những gì đang tick; mới kết nối (hoặc vừa
                // đổi môi trường) thì mới lấy lần chạy gần nhất - đừng tick lại cái người dùng vừa bỏ.
                var sameEnv = _allSolutions.Count > 0 &&
                              string.Equals(_solutionsEnvUrl, _client?.EnvironmentUrl, StringComparison.OrdinalIgnoreCase);
                List<string> wanted;
                if (CurrentProject != null)
                {
                    CurrentProject.Solutions = TargetsFor(CurrentProject);
                    wanted = CurrentProject.Solutions;
                }
                else if (sameEnv)
                    wanted = _allSolutions.Where(s => s.Enabled).Select(s => s.UniqueName).ToList();
                else
                    wanted = _settings.LastTargets ?? new List<string>();
                var previouslySelected = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);

                foreach (var s in _allSolutions) s.PropertyChanged -= Solution_PropertyChanged;

                _allSolutions = await _solutions.ListSolutionsAsync(_cts.Token);
                _solutionsEnvUrl = _client?.EnvironmentUrl;

                // Tick lại theo dự án không phải là người dùng sửa dự án.
                _loadingProject = true;
                try
                {
                    foreach (var s in _allSolutions)
                    {
                        s.Enabled = previouslySelected.Contains(s.UniqueName);
                        s.PropertyChanged += Solution_PropertyChanged;
                    }
                }
                finally { _loadingProject = false; }

                ApplySolutionFilter();
                RefreshChosen();

                if (CurrentProject != null)
                {
                    var missing = CurrentProject.Solutions
                        .Where(n => !_allSolutions.Any(s => string.Equals(s.UniqueName, n, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (missing.Count > 0)
                        Warn($"Dự án \"{CurrentProject.Name}\" có {missing.Count} solution không còn trong " +
                             "môi trường: " + string.Join(", ", missing));
                    WarnIfOtherEnvironment(CurrentProject);
                }
                Info($"Đã tải {_allSolutions.Count} solution unmanaged.");
                Status = "Đã kết nối";
            }
            catch (Exception ex)
            {
                Error("Không tải được danh sách solution: " + ex.Message);
            }
        }

        private void ApplySolutionFilter()
        {
            var filter = (SolutionFilter ?? "").Trim();
            Solutions.Clear();
            foreach (var s in _allSolutions)
            {
                if (filter.Length > 0 &&
                    (s.UniqueName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (s.FriendlyName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Solutions.Add(s);
            }
        }

        private void SetAllSolutions(bool enabled)
        {
            foreach (var s in Solutions) s.Enabled = enabled;
        }

        private void Solution_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SolutionTarget.Enabled)) return;
            RefreshChosen();
            MarkProjectDirty();
        }

        private void RefreshChosen()
        {
            ChosenSolutions.Clear();
            foreach (var s in _allSolutions.Where(s => s.Enabled)) ChosenSolutions.Add(s);

            Raise(nameof(HasChosen));
            Raise(nameof(ChosenHeader));
        }

        // ---------- component list ----------

        private void ImportList()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Chọn file danh sách component",
                Filter = "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV / TXT (*.csv;*.txt)|*.csv;*.txt|Tất cả (*.*)|*.*",
                InitialDirectory = SafeDir(_settings.LastCsvPath)
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var rows = CsvService.Load(dlg.FileName);

                // Dự án tự lưu khi đóng app / đổi dự án, nên thay danh sách mà không hỏi là mất luôn danh sách cũ.
                var replace = true;
                if (Rows.Count > 0 && rows.Count > 0)
                {
                    var where = CurrentProject == null ? "Danh sách hiện tại" : $"Dự án \"{CurrentProject.Name}\"";
                    var answer = MessageBox.Show(
                        $"{where} đang có {Rows.Count} dòng.\n\n" +
                        $"Yes = THAY bằng {rows.Count} dòng trong file\n" +
                        $"No = THÊM {rows.Count} dòng vào cuối danh sách\n" +
                        "Cancel = không nạp\n\n" +
                        "(Muốn giữ dự án cũ nguyên vẹn: bấm Cancel, tạo Dự án mới rồi nạp lại.)",
                        "Nạp danh sách", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                        MessageBoxResult.Cancel);
                    if (answer == MessageBoxResult.Cancel) return;
                    replace = answer == MessageBoxResult.Yes;
                }

                if (replace) Rows.Clear();
                foreach (var r in rows) Rows.Add(r);
                _settings.LastCsvPath = dlg.FileName;
                SaveSettings();

                if (rows.Count == 0)
                    Warn($"{Path.GetFileName(dlg.FileName)} không có dòng nào đọc được. " +
                         "Với Excel, dữ liệu phải nằm ở sheet tên 'Components' hoặc sheet đầu tiên.");
                else
                    Good($"Đã nạp {rows.Count} dòng từ {Path.GetFileName(dlg.FileName)}.");
            }
            catch (Exception ex)
            {
                Error("Đọc file thất bại: " + ex.Message);
            }
        }

        private void ClearRows()
        {
            var where = CurrentProject == null ? "" : $" của dự án \"{CurrentProject.Name}\"";
            var answer = MessageBox.Show(
                $"Xóa hết {Rows.Count} dòng{where}?\n\nDự án tự lưu khi đóng app, nên danh sách cũ sẽ mất hẳn.",
                "Xóa hết", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) Rows.Clear();
        }

        /// <summary>Ghi file template Excel đã nhúng trong exe ra đĩa rồi mở lên.</summary>
        private void DownloadTemplate()
        {
            var dlg = new SaveFileDialog
            {
                Title = "Lưu file template",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = "components-template.xlsx"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                const string resource = "DeployToSolution.components-template.xlsx";
                using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (source == null)
                {
                    Error("Không tìm thấy template nhúng trong ứng dụng.");
                    return;
                }

                using (var target = File.Create(dlg.FileName))
                    source.CopyTo(target);

                Good($"Đã lưu template: {dlg.FileName}");
                Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Error("Không lưu được template: " + ex.Message);
            }
        }

        private void PasteFromClipboard()
        {
            try
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) { Warn("Clipboard trống."); return; }
                var rows = CsvService.Parse(text);
                foreach (var r in rows) Rows.Add(r);
                Good($"Đã dán thêm {rows.Count} dòng từ clipboard.");
            }
            catch (Exception ex)
            {
                Error("Dán thất bại: " + ex.Message);
            }
        }

        private void ExportList()
        {
            var dlg = new SaveFileDialog
            {
                Title = "Xuất danh sách component",
                Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                FileName = "components.xlsx"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (IsCsv(dlg.FileName))
                {
                    CsvService.Save(dlg.FileName, Rows);
                }
                else
                {
                    ExcelWriter.Write(dlg.FileName, BuildComponentWorkbook());
                }

                Good($"Đã xuất {Rows.Count} dòng: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Error("Xuất file thất bại: " + ex.Message);
            }
        }

        /// <summary>
        /// Workbook có cấu trúc y như file template tải về: sheet Components kèm dropdown Type và Y/N,
        /// cộng sheet danh mục làm nguồn cho dropdown. Xuất ra sửa tiếp rồi nạp lại được ngay.
        /// </summary>
        private List<ExcelSheet> BuildComponentWorkbook()
        {
            var types = (_catalog != null && _catalog.TypeNames.Count > 0)
                ? _catalog.TypeNames.ToList()
                : ComponentCatalog.FriendlyTypeNames.ToList();

            var components = new List<IEnumerable<string>> { new[] { "Type", "Name", "IncludeAll" } };
            components.AddRange(Rows.Select(r => new[] { r.Type, r.Name, r.IncludeAll ? "Y" : "N" }));

            var catalogRows = new List<IEnumerable<string>> { new[] { "Type hợp lệ" } };
            catalogRows.AddRange(types.Select(t => new[] { t }));

            const string catalogSheet = "DanhMucType";

            return new List<ExcelSheet>
            {
                new ExcelSheet
                {
                    Name = "Components",
                    Rows = components,
                    ColumnWidths = new double[] { 26, 62, 12 },
                    Dropdowns =
                    {
                        new ExcelDropdown
                        {
                            Range = "A2:A1000",
                            Source = $"{catalogSheet}!$A$2:$A${types.Count + 1}"
                        },
                        new ExcelDropdown { Range = "C2:C1000", Source = "\"Y,N\"" }
                    }
                },
                new ExcelSheet
                {
                    Name = catalogSheet,
                    Rows = catalogRows,
                    ColumnWidths = new double[] { 30 }
                }
            };
        }

        private static bool IsCsv(string path) =>
            Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);

        private void SaveReport()
        {
            var dlg = new SaveFileDialog
            {
                Title = "Lưu báo cáo kết quả",
                Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                FileName = $"deploy-report-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (IsCsv(dlg.FileName))
                {
                    File.WriteAllText(dlg.FileName, CsvService.ToReport(Rows), new UTF8Encoding(true));
                }
                else
                {
                    var grid = new List<IEnumerable<string>>
                    {
                        new[] { "Type", "Name", "IncludeAll", "ComponentType", "ObjectId", "Trạng thái", "Chi tiết" }
                    };
                    grid.AddRange(Rows.Select(r => new[]
                    {
                        r.Type, r.Name, r.IncludeAll ? "Y" : "N",
                        r.ComponentType < 0 ? "" : r.ComponentType.ToString(),
                        r.ObjectId, r.StateText, r.Message
                    }));

                    ExcelWriter.Write(dlg.FileName, new[]
                    {
                        new ExcelSheet
                        {
                            Name = "KetQua",
                            Rows = grid,
                            ColumnWidths = new double[] { 22, 46, 11, 14, 38, 14, 70 }
                        }
                    });
                }

                Good("Đã lưu báo cáo " + dlg.FileName);
            }
            catch (Exception ex)
            {
                Error("Lưu báo cáo thất bại: " + ex.Message);
            }
        }

        /// <summary>Bản tóm tắt gom theo loại để dán vào ticket deploy. Không cần kết nối.</summary>
        private void ShowDeployReport()
        {
            try
            {
                var targets = _allSolutions.Where(s => s.Enabled).Select(s => s.UniqueName).ToList();
                var environment = ConnectedUrl ?? EnvironmentUrl;
                var text = DeployReport.Build(Rows, _catalog, targets, environment);
                var grid = DeployReport.ToGrid(Rows, _catalog, targets, environment);

                var window = new ReportWindow(text, grid) { Owner = Application.Current?.MainWindow };
                window.ShowDialog();

                Info($"Đã dựng báo cáo deploy cho {Rows.Count(r => r.Include)} component.");
            }
            catch (Exception ex)
            {
                Error("Không dựng được báo cáo: " + ex.Message);
            }
        }

        private void CopyLog()
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, Log.Select(l => l.Display)));
                Info("Đã copy log vào clipboard.");
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        private void OpenLogFolder() => OpenFolder(Path.GetDirectoryName(SettingsStore.LogFile));

        /// <summary>
        /// Nạp và lọc gợi ý cho cột Name theo Type của dòng, gọi lại mỗi lần người dùng gõ.
        /// Danh sách đầy đủ được cache trong catalog; ở đây chỉ lọc lại theo chữ đang gõ.
        /// </summary>
        /// <returns>
        /// False khi không đổ gợi ý nào, kể cả khi kết quả về muộn hơn một lượt gõ mới hơn - lúc đó
        /// người gọi không được đụng vào ô chữ, người dùng đã gõ tiếp.
        /// </returns>
        public async Task<bool> LoadNameSuggestionsAsync(ComponentRow row, string currentText = null)
        {
            if (row == null) return false;
            var ticket = ++row.SuggestionTicket;

            if (_catalog == null || !IsConnected) return false;

            var text = currentText ?? row.Name ?? "";
            var key = _catalog.SuggestionKey(row.Type, text);
            if (string.IsNullOrEmpty(key)) return false;

            List<string> all;
            try
            {
                all = await _catalog.SuggestNamesAsync(row.Type, text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Ghi một lần cho mỗi khoá rồi thôi, tránh spam log theo từng phím gõ.
                if (row.SuggestionKey != key)
                {
                    row.SuggestionKey = key;
                    Info($"Không lấy được gợi ý cho Type '{row.Type}': {ex.Message}");
                }
                return false;
            }

            // Trong lúc chờ mạng người dùng đã gõ tiếp: kết quả này ứng với chữ cũ, đừng đè lên gợi ý mới.
            if (ticket != row.SuggestionTicket) return false;

            row.SuggestionKey = key;

            var matches = string.IsNullOrWhiteSpace(text)
                ? all
                : all.Where(n => n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

            var shown = matches.Take(200).ToList();

            if (row.NameSuggestions.SequenceEqual(shown, StringComparer.Ordinal)) return true;

            row.NameSuggestions.Clear();
            foreach (var n in shown) row.NameSuggestions.Add(n);
            return true;
        }

        /// <summary>Báo cho view biết dòng vừa thêm, để cuộn tới và chọn sẵn.</summary>
        public event Action<ComponentRow> RowAdded;

        /// <summary>
        /// Mở popup thêm dòng: chọn một Type rồi tick nhiều tên cùng lúc, thay vì thêm từng dòng
        /// trống rồi gõ lại từ đầu.
        /// </summary>
        private void AddRow()
        {
            // Đã đăng xuất thì catalog cũ không còn token: đưa null để popup chạy chế độ gõ tay.
            var dialog = new AddRowsWindow(TypeNames, IsConnected ? _catalog : null, Info)
            {
                Owner = Application.Current?.MainWindow
            };
            dialog.RowsRequested += InsertRows;
            try { dialog.ShowDialog(); }
            finally { dialog.RowsRequested -= InsertRows; }
        }

        /// <summary>Cả mẻ mới nằm trên đầu, giữ nguyên thứ tự đã chọn, để khỏi cuộn xuống cuối danh sách.</summary>
        private void InsertRows(List<ComponentRow> rows)
        {
            // Bỏ tên đã có: add trùng một component vào cùng solution không thêm được gì.
            var seen = new HashSet<string>(Rows.Select(RowKey), StringComparer.OrdinalIgnoreCase);
            var fresh = rows.Where(r => seen.Add(RowKey(r))).ToList();

            for (var i = 0; i < fresh.Count; i++) Rows.Insert(i, fresh[i]);
            if (fresh.Count > 0) RowAdded?.Invoke(fresh[0]);

            var skipped = rows.Count - fresh.Count;
            if (fresh.Count == 0)
                Warn($"Cả {rows.Count} tên đều đã có trong danh sách, không thêm dòng nào.");
            else if (skipped > 0)
                Good($"Đã thêm {fresh.Count} dòng, bỏ qua {skipped} tên đã có trong danh sách.");
            else
                Good($"Đã thêm {fresh.Count} dòng.");
        }

        private static string RowKey(ComponentRow row) =>
            $"{ComponentCatalog.CanonicalKey(row.Type)}|{(row.Name ?? "").Trim()}";

        /// <summary>
        /// Đang chạy thì không cho xoá/nhân đôi/sửa dòng: vòng lặp đã chốt danh sách từ trước,
        /// xoá một dòng khỏi màn hình lúc này không ngăn được nó bị add.
        /// </summary>
        private bool BlockedWhileBusy()
        {
            if (!IsBusy) return false;
            Warn("Đang chạy - chờ xong (hoặc bấm Hủy) rồi mới sửa danh sách.");
            return true;
        }

        public void RemoveRows(IEnumerable<ComponentRow> rows)
        {
            if (BlockedWhileBusy()) return;
            foreach (var r in rows.ToList()) Rows.Remove(r);
        }

        public void SetInclude(IEnumerable<ComponentRow> rows, bool include)
        {
            if (BlockedWhileBusy()) return;
            foreach (var r in rows.ToList()) r.Include = include;
        }

        /// <summary>Chèn bản sao ngay dưới dòng gốc, tiện khi thêm nhiều component cùng loại.</summary>
        public void DuplicateRows(IEnumerable<ComponentRow> rows)
        {
            if (BlockedWhileBusy()) return;
            foreach (var r in rows.ToList())
            {
                var at = Rows.IndexOf(r);
                if (at < 0) continue;
                Rows.Insert(at + 1, r.Clone());
            }
        }

        // ---------- dự án deploy ----------

        /// <summary>
        /// Mỗi hotfix là một dự án: danh sách component riêng cộng các solution đích riêng.
        /// Tự lưu khi đổi dự án, khi deploy xong và khi đóng app — không có hộp thoại "lưu chưa?"
        /// để không bao giờ mất công dựng lại danh sách.
        /// </summary>
        public ObservableCollection<DeployProject> Projects { get; } = new ObservableCollection<DeployProject>();

        private DeployProject _currentProject;
        public DeployProject CurrentProject
        {
            get => _currentProject;
            set
            {
                if (_switchingProject || ReferenceEquals(_currentProject, value)) return;

                SaveCurrentProject(quiet: true);   // giữ lại việc đang làm dở trước khi đi chỗ khác

                _currentProject = value;
                Raise(nameof(CurrentProject));

                if (value != null) ApplyProject(value);
                Raise(nameof(ProjectTitle));
            }
        }

        private bool _switchingProject;   // chính code này đang đổi -> đừng coi là người dùng chọn
        private bool _loadingProject;     // đang đổ dữ liệu vào -> đừng đánh dấu là đã sửa

        private bool _isProjectDirty;
        public bool IsProjectDirty
        {
            get => _isProjectDirty;
            set { if (Set(ref _isProjectDirty, value)) Raise(nameof(ProjectTitle)); }
        }

        public string ProjectTitle => CurrentProject == null
            ? "(chưa chọn dự án)"
            : CurrentProject.Name + (IsProjectDirty ? "  •  chưa lưu" : "");

        private void LoadProjects()
        {
            Projects.Clear();
            var loaded = ProjectStore.LoadAll(out var broken);
            foreach (var project in loaded) Projects.Add(project);

            if (broken.Count > 0)
                Warn($"Không đọc được {broken.Count} file dự án (JSON hỏng?): {string.Join(", ", broken)}. " +
                     $"File vẫn nằm trong {ProjectStore.Dir}, sửa tay rồi mở lại app.");
        }

        private void SetCurrentQuietly(DeployProject project)
        {
            _switchingProject = true;
            try
            {
                _currentProject = project;
                Raise(nameof(CurrentProject));
                Raise(nameof(ProjectTitle));
            }
            finally { _switchingProject = false; }
        }

        private void OpenProject(DeployProject project)
        {
            SetCurrentQuietly(project);
            ApplyProject(project);
        }

        private void ApplyProject(DeployProject project)
        {
            _loadingProject = true;
            try
            {
                Rows.Clear();
                foreach (var row in project.Rows)
                    Rows.Add(new ComponentRow
                    {
                        Include = row.Include, Type = row.Type, Name = row.Name, IncludeAll = row.IncludeAll
                    });

                AddRequiredComponents = project.AddRequiredComponents;
                CleanupArchival = project.CleanupArchival;

                // Chỉ điền URL khi ô đang trống: đang kết nối rồi thì đừng giật môi trường khỏi tay người dùng.
                if (!string.IsNullOrWhiteSpace(project.EnvironmentUrl) && string.IsNullOrWhiteSpace(EnvironmentUrl))
                    EnvironmentUrl = project.EnvironmentUrl;

                ApplyProjectSolutions(project);
            }
            finally { _loadingProject = false; }

            IsProjectDirty = false;
            _settings.LastProject = project.Name;
            SaveSettings();

            Good($"Dự án \"{project.Name}\": {project.Rows.Count} component, {project.Solutions.Count} solution đích.");
        }

        /// <summary>Tick lại đúng solution của dự án. Chưa tải danh sách thì để lúc kết nối xong làm.</summary>
        private void ApplyProjectSolutions(DeployProject project)
        {
            if (_allSolutions.Count == 0) return;

            var wanted = new HashSet<string>(project.Solutions, StringComparer.OrdinalIgnoreCase);
            foreach (var s in _allSolutions) s.Enabled = wanted.Contains(s.UniqueName);
            RefreshChosen();

            var missing = project.Solutions
                .Where(n => !_allSolutions.Any(s => string.Equals(s.UniqueName, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
                Warn($"Dự án \"{project.Name}\" có {missing.Count} solution không còn trong môi trường: " +
                     string.Join(", ", missing));
            WarnIfOtherEnvironment(project);
        }

        /// <summary>
        /// Solution đích của dự án tính theo màn hình. Chưa tải danh sách solution (chưa kết nối, vừa
        /// đăng xuất) thì không biết người dùng tick gì, nên giữ nguyên của dự án; solution không có
        /// trong môi trường đang kết nối cũng giữ lại, chỉ bỏ những cái người dùng thật sự bỏ tick.
        /// </summary>
        private List<string> TargetsFor(DeployProject project)
        {
            var saved = project?.Solutions ?? new List<string>();
            if (_allSolutions.Count == 0) return saved.ToList();

            var known = new HashSet<string>(_allSolutions.Select(s => s.UniqueName), StringComparer.OrdinalIgnoreCase);
            return saved.Where(n => !known.Contains(n))
                .Concat(_allSolutions.Where(s => s.Enabled).Select(s => s.UniqueName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Môi trường thật sự đang kết nối; ô URL có thể đã bị sửa sau khi kết nối.</summary>
        private string ConnectedUrl => IsConnected && _client != null ? _client.EnvironmentUrl : null;

        /// <summary>Dự án được lập cho một môi trường khác môi trường đang kết nối.</summary>
        private bool IsOtherEnvironment(DeployProject project) =>
            project != null && ConnectedUrl != null && !string.IsNullOrWhiteSpace(project.EnvironmentUrl) &&
            !string.Equals(NormalizeUrl(project.EnvironmentUrl), ConnectedUrl, StringComparison.OrdinalIgnoreCase);

        private void WarnIfOtherEnvironment(DeployProject project)
        {
            if (IsOtherEnvironment(project))
                Warn($"Dự án \"{project.Name}\" được lập cho {project.EnvironmentUrl}, nhưng đang kết nối " +
                     $"{ConnectedUrl}. Solution đích được tick theo tên - kiểm tra kỹ trước khi ADD.");
        }

        private void CaptureInto(DeployProject project)
        {
            // Môi trường của dự án chỉ gán lần đầu, hoặc sau một lần deploy thật (RunAsync). Kết nối nhầm
            // sang môi trường khác rồi đóng app không được âm thầm gắn dự án sang môi trường đó - không thì
            // cảnh báo "khác môi trường" lúc bấm ADD mất tác dụng.
            if (string.IsNullOrWhiteSpace(project.EnvironmentUrl)) project.EnvironmentUrl = ConnectedUrl ?? EnvironmentUrl;
            project.Solutions = TargetsFor(project);
            project.AddRequiredComponents = AddRequiredComponents;
            project.CleanupArchival = CleanupArchival;
            project.Rows = Rows.Select(r => new ProjectRow
            {
                Include = r.Include, Type = r.Type, Name = r.Name, IncludeAll = r.IncludeAll
            }).ToList();
        }

        public void SaveCurrentProject(bool quiet = false)
        {
            if (CurrentProject == null) return;

            try
            {
                CaptureInto(CurrentProject);
                ProjectStore.Save(CurrentProject);
                IsProjectDirty = false;
                if (!quiet) Good($"Đã lưu dự án \"{CurrentProject.Name}\".");
            }
            catch (Exception ex)
            {
                Error("Không lưu được dự án: " + ex.Message);
            }
        }

        private void SaveProject() => SaveCurrentProject();

        private void NewProject()
        {
            var name = AskProjectName("Dự án mới", $"Deploy {DateTime.Now:dd-MM-yyyy}");
            if (name == null) return;

            SaveCurrentProject(quiet: true);

            var project = new DeployProject
            {
                Name = name,
                EnvironmentUrl = ConnectedUrl ?? EnvironmentUrl,
                AddRequiredComponents = AddRequiredComponents,
                CleanupArchival = CleanupArchival,
                Solutions = TargetsFor(CurrentProject)
            };

            try { ProjectStore.Save(project); }
            catch (Exception ex) { Error("Không tạo được dự án: " + ex.Message); return; }

            Projects.Insert(0, project);
            SetCurrentQuietly(project);

            _loadingProject = true;
            try { Rows.Clear(); }          // dự án mới bắt đầu từ danh sách trống
            finally { _loadingProject = false; }

            IsProjectDirty = false;
            _settings.LastProject = name;
            SaveSettings();
            Good($"Đã tạo dự án \"{name}\". Solution đang tick được giữ nguyên, danh sách component để trống.");
        }

        /// <summary>Nhân bản danh sách đang có sang một dự án mới - tiện khi hotfix sau giống hotfix trước.</summary>
        private void SaveProjectAs()
        {
            var suggestion = CurrentProject == null ? $"Deploy {DateTime.Now:dd-MM-yyyy}" : CurrentProject.Name + " (copy)";
            var name = AskProjectName("Lưu thành dự án mới", suggestion);
            if (name == null) return;

            var project = new DeployProject
            {
                Name = name,
                Note = CurrentProject?.Note ?? "",
                EnvironmentUrl = CurrentProject?.EnvironmentUrl ?? "",
                Solutions = CurrentProject?.Solutions.ToList() ?? new List<string>()
            };
            CaptureInto(project);

            try { ProjectStore.Save(project); }
            catch (Exception ex) { Error("Không lưu được dự án: " + ex.Message); return; }

            Projects.Insert(0, project);
            SetCurrentQuietly(project);
            IsProjectDirty = false;
            _settings.LastProject = name;
            SaveSettings();
            Good($"Đã lưu thành dự án \"{name}\" với {Rows.Count} component.");
        }

        private void RenameProject()
        {
            var project = CurrentProject;
            if (project == null) return;

            var name = AskProjectName("Đổi tên dự án", project.Name, project.Name);
            if (name == null || name == project.Name) return;

            var oldName = project.Name;
            try
            {
                project.Name = name;
                ProjectStore.Save(project);
                // Chỉ đổi hoa/thường thì hai tên là CÙNG một file trên Windows - xoá là mất luôn file vừa ghi.
                if (!ProjectStore.SameFile(oldName, name)) ProjectStore.Delete(oldName);
            }
            catch (Exception ex)
            {
                project.Name = oldName;
                Error("Không đổi tên được: " + ex.Message);
                return;
            }

            // ComboBox hiện Name, mà Name không phải thuộc tính quan sát được -> dựng lại mục đó.
            // Bỏ mục đang chọn thì ComboBox đẩy null về CurrentProject; chặn lại, đây không phải người dùng chọn.
            var at = Projects.IndexOf(project);
            if (at >= 0)
            {
                _switchingProject = true;
                try { Projects.RemoveAt(at); Projects.Insert(at, project); }
                finally { _switchingProject = false; }
            }
            SetCurrentQuietly(project);

            _settings.LastProject = name;
            SaveSettings();
            Good($"Đã đổi tên dự án \"{oldName}\" thành \"{name}\".");
        }

        private void DeleteProject()
        {
            var project = CurrentProject;
            if (project == null) return;

            var answer = MessageBox.Show(
                $"Xóa dự án \"{project.Name}\"?\n\n" +
                $"{project.Rows.Count} component sẽ mất khỏi ổ đĩa. Danh sách đang hiện trên màn hình vẫn còn.",
                "Xóa dự án", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            // Bỏ chọn TRƯỚC khi bỏ khỏi Projects: bỏ mục đang chọn thì ComboBox đẩy null về CurrentProject,
            // setter lại lưu dự án hiện tại trước khi đổi - tức là ghi lại đúng file vừa xoá.
            SetCurrentQuietly(null);
            Projects.Remove(project);
            ProjectStore.Delete(project.Name);

            IsProjectDirty = false;
            _settings.LastProject = "";
            SaveSettings();
            Warn($"Đã xóa dự án \"{project.Name}\".");
        }

        private string AskProjectName(string title, string suggestion, string allowSame = null)
        {
            return PromptWindow.Ask(Application.Current?.MainWindow, title,
                "Tên dự án — thường là mã ticket hoặc tên hotfix:",
                suggestion,
                $"Mỗi dự án là một file .json trong {ProjectStore.Dir}",
                name =>
                {
                    if (string.IsNullOrWhiteSpace(name)) return "Tên dự án không được để trống.";

                    // Đổi tên chính nó (kể cả chỉ đổi hoa/thường) thì trùng file là đương nhiên.
                    if (allowSame != null && ProjectStore.SameFile(name, allowSame)) return null;

                    // So theo file thật chứ không theo chữ: "a/b" và "a_b" cùng ra a_b.json, và file
                    // JSON hỏng (không hiện trong danh sách) cũng không được bị ghi đè.
                    var clash = Projects.FirstOrDefault(p => ProjectStore.SameFile(p.Name, name));
                    if (clash != null) return $"Tên này trùng file với dự án \"{clash.Name}\".";
                    if (ProjectStore.Exists(name))
                        return $"Đã có file {Path.GetFileName(ProjectStore.FileFor(name))} trong thư mục dự án.";
                    return null;
                });
        }

        private void Rows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (ComponentRow row in e.OldItems) row.PropertyChanged -= Row_PropertyChanged;
            if (e.NewItems != null)
                foreach (ComponentRow row in e.NewItems) row.PropertyChanged += Row_PropertyChanged;

            MarkProjectDirty();
        }

        /// <summary>ObjectId / trạng thái / thông báo đổi liên tục lúc chạy, không phải nội dung dự án.</summary>
        private static readonly HashSet<string> ProjectFields = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ComponentRow.Include), nameof(ComponentRow.Type),
            nameof(ComponentRow.Name), nameof(ComponentRow.IncludeAll)
        };

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ProjectFields.Contains(e.PropertyName)) MarkProjectDirty();
        }

        private void MarkProjectDirty()
        {
            if (_loadingProject) return;
            IsProjectDirty = true;
        }

        private void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        // ---------- resolve + run ----------

        private enum ResolveOutcome { AllFound, SomeMissing, Stopped }

        private async Task<ResolveOutcome> ResolveAsync()
        {
            var targets = Rows.Where(r => r.Include).ToList();
            if (targets.Count == 0) { Warn("Không có dòng nào được chọn."); return ResolveOutcome.Stopped; }

            IsBusy = true;
            _cts = new CancellationTokenSource();
            Progress = 0;
            ProgressMax = targets.Count;
            Step($"Kiểm tra {targets.Count} component...");

            var ok = 0;
            var bad = 0;
            try
            {
                foreach (var row in targets)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Status = $"Đang tìm: {row.Name}";

                    if (row.IsResolved) { ok++; Progress++; continue; }

                    var result = await _catalog.ResolveAsync(row, _cts.Token);
                    row.ObjectId = result.ObjectId;
                    row.MatchedName = result.MatchedName;
                    row.ComponentType = result.ComponentType;
                    row.State = result.State;
                    row.Message = result.Message;

                    if (result.Ok)
                    {
                        ok++;
                    }
                    else
                    {
                        bad++;
                        Error($"{row.Type} \"{row.Name}\": {result.Message}");
                    }
                    Progress++;
                }
            }
            catch (OperationCanceledException)
            {
                // Hủy là dừng hẳn: đừng để RunAsync coi "chưa gặp dòng lỗi nào" là "tìm đủ" rồi chạy tiếp.
                Warn($"Đã hủy khi đang kiểm tra ({ok}/{targets.Count} đã tìm thấy).");
                return ResolveOutcome.Stopped;
            }
            catch (Exception ex)
            {
                Error(ex.Message);
                return ResolveOutcome.Stopped;
            }
            finally
            {
                IsBusy = false;
                Status = "Đã kết nối";
            }

            if (bad == 0) Good($"Tìm thấy đủ {ok}/{targets.Count} component.");
            else Warn($"Tìm thấy {ok}/{targets.Count}, còn {bad} dòng lỗi - sửa rồi chạy lại.");
            return bad == 0 ? ResolveOutcome.AllFound : ResolveOutcome.SomeMissing;
        }

        private async Task RunAsync(bool dryRun)
        {
            var selectedSolutions = _allSolutions.Where(s => s.Enabled).ToList();
            if (selectedSolutions.Count == 0)
            {
                Warn("Chưa chọn solution đích nào ở cột bên trái.");
                return;
            }

            var rows = Rows.Where(r => r.Include).ToList();
            if (rows.Count == 0) { Warn("Không có dòng nào được chọn."); return; }

            var outcome = await ResolveAsync();
            if (outcome == ResolveOutcome.Stopped) return;
            if (outcome == ResolveOutcome.SomeMissing)
            {
                var answer = MessageBox.Show(
                    "Có dòng chưa tìm được component. Vẫn tiếp tục với những dòng hợp lệ?",
                    "Còn lỗi", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            var valid = rows.Where(r => r.IsResolved).ToList();
            if (valid.Count == 0) { Warn("Không có dòng hợp lệ để add."); return; }

            // Chốt mọi thứ lúc bắt đầu: đổi dự án / tùy chọn giữa chừng không được làm lệch lần chạy này.
            var project = CurrentProject;
            var addRequired = AddRequiredComponents;
            var cleanupArchival = CleanupArchival;
            var environment = ConnectedUrl ?? EnvironmentUrl;

            if (!dryRun)
            {
                // Solution được tick lại tự động theo unique name, mà DEV và UAT hay có solution trùng tên -
                // nên phải nói rõ đang ghi vào môi trường nào, bằng tài khoản nào.
                var otherEnv = IsOtherEnvironment(project);
                var warning = otherEnv
                    ? $"CẢNH BÁO: dự án \"{project.Name}\" được lập cho {project.EnvironmentUrl}, " +
                      "KHÁC môi trường đang kết nối.\n\n"
                    : "";

                var confirm = MessageBox.Show(
                    warning +
                    $"Môi trường : {environment}\n" +
                    $"Tài khoản  : {ConnectedAs}\n\n" +
                    $"Add {valid.Count} component vào {selectedSolutions.Count} solution:\n\n" +
                    string.Join("\n", selectedSolutions.Select(s => "  - " + s.UniqueName)) +
                    "\n\nTiếp tục?",
                    "Xác nhận", MessageBoxButton.OKCancel,
                    otherEnv ? MessageBoxImage.Warning : MessageBoxImage.Question,
                    otherEnv ? MessageBoxResult.Cancel : MessageBoxResult.OK);
                if (confirm != MessageBoxResult.OK) return;
            }

            // Trạng thái cuối của một dòng là trạng thái tệ nhất qua mọi solution, tính lại từ đầu mỗi lần chạy.
            foreach (var row in valid) row.State = RowState.Resolved;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            Progress = 0;
            ProgressMax = valid.Count * selectedSolutions.Count;

            var notes = valid.ToDictionary(r => r, _ => new List<string>());
            int added = 0, already = 0, failed = 0;

            Step(dryRun
                ? $"THỬ (dry run): {valid.Count} component x {selectedSolutions.Count} solution"
                : $"BẮT ĐẦU ADD: {valid.Count} component x {selectedSolutions.Count} solution");

            try
            {
                foreach (var solution in selectedSolutions)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Step($"Solution: {solution.UniqueName}");

                    string solutionId;
                    try
                    {
                        solutionId = await _solutions.GetSolutionIdAsync(solution.UniqueName, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Error($"{solution.UniqueName}: {ex.Message}");
                        Progress += valid.Count;
                        continue;
                    }

                    foreach (var row in valid)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        Status = $"{solution.UniqueName} <- {row.Name}";

                        try
                        {
                            var present = await _solutions.ContainsAsync(
                                solutionId, row.ObjectId, row.ComponentType, _cts.Token);

                            // IncludeAll chỉ có nghĩa với Table; dòng khác lỡ tick thì coi như không tick.
                            var isEntity = _catalog != null && row.ComponentType == _catalog.EntityComponentType;
                            var includeAll = isEntity && row.IncludeAll;

                            if (present && !includeAll)
                            {
                                already++;
                                notes[row].Add($"{solution.UniqueName}: đã có");
                                Promote(row, RowState.AlreadyIn);
                                Info($"  [đã có] {row.Type} {row.Name}");
                            }
                            else if (dryRun)
                            {
                                added++;
                                var what = present ? "sẽ cập nhật (include all)" : "sẽ add";
                                notes[row].Add($"{solution.UniqueName}: {what}");
                                Info($"  [{what}] {row.Type} {row.Name}");
                            }
                            else
                            {
                                // Chỉ Entity mới chấp nhận DoNotIncludeSubcomponents.
                                bool? subcomponents = isEntity ? !includeAll : (bool?)null;

                                await _solutions.AddAsync(solution.UniqueName, row.ObjectId, row.ComponentType,
                                    subcomponents, addRequired, _cts.Token);
                                added++;
                                var what = present ? "đã cập nhật (include all)" : "đã add";
                                notes[row].Add($"{solution.UniqueName}: {what}");
                                Promote(row, RowState.Added);
                                Good($"  [{what}] {row.Type} {row.Name}");
                            }

                            if (cleanupArchival)
                                await CleanupArchivalAsync(solution, solutionId, row, includeAll, dryRun, notes[row]);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failed++;
                            notes[row].Add($"{solution.UniqueName}: LỖI {ex.Message}");
                            Promote(row, RowState.Failed);
                            Error($"  [lỗi] {row.Type} {row.Name}: {ex.Message}");
                        }
                        finally
                        {
                            Progress++;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { Warn("Đã hủy giữa chừng."); }
            catch (Exception ex) { Error(ex.Message); }
            finally
            {
                foreach (var kv in notes)
                    if (kv.Value.Count > 0) kv.Key.Message = string.Join(" | ", kv.Value);

                IsBusy = false;
                Status = "Đã kết nối";

                _settings.LastTargets = selectedSolutions.Select(s => s.UniqueName).ToList();
                _settings.AddRequiredComponents = addRequired;
                _settings.CleanupArchival = cleanupArchival;
                SaveSettings();
            }

            var verb = dryRun ? "sẽ add" : "đã add";
            var summary = $"XONG - {verb}: {added}, đã có sẵn: {already}, lỗi: {failed}.";
            if (failed > 0) Error(summary); else Good(summary);

            // Ghi lại lần deploy thật vào đúng dự án đã chạy (không phải dự án đang mở lúc này).
            if (!dryRun && project != null)
            {
                project.EnvironmentUrl = environment;   // người dùng đã xác nhận deploy vào đây
                project.LastDeployedUtc = DateTime.UtcNow;
                project.LastResult =
                    $"add {added}, có sẵn {already}, lỗi {failed} · {selectedSolutions.Count} solution";
                if (ReferenceEquals(project, CurrentProject)) SaveCurrentProject(quiet: true);
                else
                {
                    try { ProjectStore.Save(project); }
                    catch (Exception ex) { Error($"Không lưu được kết quả vào dự án \"{project.Name}\": {ex.Message}"); }
                }
            }
        }

        private static int Rank(RowState state) => state switch
        {
            RowState.Failed => 3,
            RowState.Added => 2,
            RowState.AlreadyIn => 1,
            _ => 0
        };

        /// <summary>Chỉ nâng trạng thái lên mức tệ hơn: lỗi ở solution A không bị "đã add" ở solution B che mất.</summary>
        private static void Promote(ComponentRow row, RowState state)
        {
            if (Rank(state) > Rank(row.State)) row.State = state;
        }

        /// <summary>
        /// Dataverse tự kéo bản ghi MetadataForArchival (metadata retention) của table vào solution,
        /// tạo ra một dòng thứ hai cùng tên. Gỡ những bản ghi chưa bật retention để solution hotfix
        /// chỉ chứa đúng thứ đã khai trong CSV. Mọi bước đều ghi log để lần chạy không gỡ được gì
        /// vẫn nói rõ lý do.
        /// </summary>
        private async Task CleanupArchivalAsync(SolutionTarget solution, string solutionId,
            ComponentRow row, bool includeAll, bool dryRun, List<string> note)
        {
            if (_catalog == null) return;
            if (row.ComponentType != _catalog.EntityComponentType) return;

            if (includeAll)
            {
                Info($"  · archival: bỏ qua {row.Name} vì IncludeAll đang bật");
                return;
            }

            ArchivalScan scan;
            try
            {
                scan = await _solutions.ScanArchivalAsync(solutionId, row.ObjectId, row.MatchedName, _cts.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Warn($"  · archival: dò thất bại cho {row.Name}: {ex.Message}");
                return;
            }

            foreach (var n in scan.Notes) Info($"  · archival: {n}");

            if (scan.Removable.Count == 0) return;

            foreach (var stray in scan.Removable)
            {
                if (dryRun)
                {
                    Warn($"  [sẽ gỡ] MetadataForArchival \"{stray.Name}\" (componenttype {stray.ComponentType})");
                    note.Add($"{solution.UniqueName}: sẽ gỡ MetadataForArchival");
                    continue;
                }

                // Tài liệu không nói rõ SolutionComponent nhận objectid hay khoá chính của dòng
                // solutioncomponent, nên thử cả hai và xác minh lại sau mỗi lần.
                var candidates = new List<(string Label, string Id)>
                {
                    ("objectid", stray.ObjectId),
                    ("solutioncomponentid", stray.SolutionComponentId)
                };

                var removed = false;
                string lastProblem = null;

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate.Id)) continue;

                    try
                    {
                        await _solutions.RemoveAsync(
                            solution.UniqueName, candidate.Id, stray.ComponentType, _cts.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastProblem = $"gỡ bằng {candidate.Label} lỗi: {ex.Message}";
                        Info($"  · archival: {lastProblem}");
                        continue;
                    }

                    // RemoveSolutionComponent có thể trả 204 mà không gỡ gì, nên phải đọc lại solution.
                    if (!await _solutions.IsInSolutionAsync(solutionId, stray.ObjectId, _cts.Token))
                    {
                        Good($"  [gỡ] MetadataForArchival \"{stray.Name}\" bằng {candidate.Label} " +
                             "- xác nhận đã biến mất khỏi solution");
                        note.Add($"{solution.UniqueName}: đã gỡ MetadataForArchival");
                        removed = true;
                        break;
                    }

                    lastProblem = $"gỡ bằng {candidate.Label} không báo lỗi nhưng component vẫn còn";
                    Info($"  · archival: {lastProblem}");
                }

                if (!removed)
                {
                    Warn($"  [không gỡ được] MetadataForArchival \"{stray.Name}\" " +
                         $"(componenttype {stray.ComponentType}, rootcomponentbehavior={stray.RootBehavior ?? "null"}). " +
                         $"Lần cuối: {lastProblem ?? "không rõ"}");
                    note.Add($"{solution.UniqueName}: không gỡ được MetadataForArchival");
                }
            }
        }

        // ---------- misc ----------

        private void RememberEnvironment(string url)
        {
            if (RecentEnvironments.Contains(url)) RecentEnvironments.Remove(url);
            RecentEnvironments.Insert(0, url);
            while (RecentEnvironments.Count > 8) RecentEnvironments.RemoveAt(RecentEnvironments.Count - 1);
        }

        public void SaveSettings()
        {
            _settings.EnvironmentUrl = EnvironmentUrl;
            _settings.Tenant = Tenant;
            _settings.ClientId = ClientId;
            _settings.AuthMode = UseClientSecret ? "ClientSecret" : "DeviceCode";
            _settings.RememberSignIn = RememberSignIn;
            _settings.AddRequiredComponents = AddRequiredComponents;
            _settings.CleanupArchival = CleanupArchival;
            _settings.RecentEnvironments = RecentEnvironments.ToList();
            SettingsStore.Save(_settings);
        }

        private static string SafeDir(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                return Directory.Exists(dir) ? dir : "";
            }
            catch { return ""; }
        }

        private static string NormalizeUrl(string input)
        {
            input = (input ?? "").Trim();
            if (input.Length == 0) return null;
            if (!input.StartsWith("http", StringComparison.OrdinalIgnoreCase)) input = "https://" + input;
            return Uri.TryCreate(input, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? $"{uri.Scheme}://{uri.Host}"
                : null;
        }

        private static void OpenBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void Write(LogLevel level, string text)
        {
            SettingsStore.AppendLog($"{DateTime.Now:HH:mm:ss}  [{level}] {text}");

            void Add() => Log.Add(new LogLine { Level = level, Text = text });
            if (Application.Current?.Dispatcher.CheckAccess() == false)
                Application.Current.Dispatcher.Invoke(Add);
            else
                Add();
        }

        private void Info(string text) => Write(LogLevel.Info, text);
        private void Good(string text) => Write(LogLevel.Good, text);
        private void Warn(string text) => Write(LogLevel.Warn, text);
        private void Error(string text) => Write(LogLevel.Error, text);
        private void Step(string text) => Write(LogLevel.Step, text);
    }
}
