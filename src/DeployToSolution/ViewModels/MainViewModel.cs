using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
            ClearRowsCommand = new RelayCommand(_ => Rows.Clear(), _ => Rows.Count > 0 && !IsBusy);
            ResolveCommand = new RelayCommand(async _ => await ResolveAsync(), _ => IsConnected && !IsBusy && Rows.Count > 0);
            RunCommand = new RelayCommand(async _ => await RunAsync(false), _ => IsConnected && !IsBusy && Rows.Count > 0);
            DryRunCommand = new RelayCommand(async _ => await RunAsync(true), _ => IsConnected && !IsBusy && Rows.Count > 0);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
            SaveReportCommand = new RelayCommand(_ => SaveReport(), _ => Rows.Count > 0);
            DeployReportCommand = new RelayCommand(_ => ShowDeployReport(), _ => Rows.Count > 0);
            CopyLogCommand = new RelayCommand(_ => CopyLog(), _ => Log.Count > 0);
            OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
            OpenVerificationCommand = new RelayCommand(_ => OpenBrowser(VerificationUri));
            SelectAllSolutionsCommand = new RelayCommand(_ => SetAllSolutions(true));
            SelectNoSolutionsCommand = new RelayCommand(_ => SetAllSolutions(false));

            Info("Sẵn sàng. Nhập URL môi trường rồi bấm Kết nối.");
        }

        // ---------- state ----------

        public ObservableCollection<ComponentRow> Rows { get; } = new ObservableCollection<ComponentRow>();
        public ObservableCollection<SolutionTarget> Solutions { get; } = new ObservableCollection<SolutionTarget>();
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
        public bool AddRequiredComponents { get => _addRequiredComponents; set => Set(ref _addRequiredComponents, value); }

        private bool _cleanupArchival = true;
        public bool CleanupArchival { get => _cleanupArchival; set => Set(ref _cleanupArchival, value); }

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
            _allSolutions.Clear();
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

                var previouslySelected = new HashSet<string>(
                    _allSolutions.Where(s => s.Enabled).Select(s => s.UniqueName)
                        .Concat(_settings.LastTargets ?? new List<string>()),
                    StringComparer.OrdinalIgnoreCase);

                _allSolutions = await _solutions.ListSolutionsAsync(_cts.Token);
                foreach (var s in _allSolutions)
                    s.Enabled = previouslySelected.Contains(s.UniqueName);

                ApplySolutionFilter();
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
                Rows.Clear();
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
                var text = DeployReport.Build(Rows, _catalog, targets, EnvironmentUrl);
                var grid = DeployReport.ToGrid(Rows, _catalog, targets, EnvironmentUrl);

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

        private void OpenLogFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(SettingsStore.LogFile);
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        /// <summary>
        /// Nạp và lọc gợi ý cho cột Name theo Type của dòng, gọi lại mỗi lần người dùng gõ.
        /// Danh sách đầy đủ được cache trong catalog; ở đây chỉ lọc lại theo chữ đang gõ.
        /// </summary>
        public async Task LoadNameSuggestionsAsync(ComponentRow row, string currentText = null)
        {
            if (row == null || _catalog == null || !IsConnected) return;

            var text = currentText ?? row.Name ?? "";
            var key = _catalog.SuggestionKey(row.Type, text);
            if (string.IsNullOrEmpty(key)) return;

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
                return;
            }

            row.SuggestionKey = key;

            var matches = string.IsNullOrWhiteSpace(text)
                ? all
                : all.Where(n => n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

            var shown = matches.Take(200).ToList();

            if (row.NameSuggestions.SequenceEqual(shown, StringComparer.Ordinal)) return;

            row.NameSuggestions.Clear();
            foreach (var n in shown) row.NameSuggestions.Add(n);
        }

        /// <summary>Báo cho view biết dòng vừa thêm, để cuộn tới và chọn sẵn.</summary>
        public event Action<ComponentRow> RowAdded;

        /// <summary>
        /// Mở popup thêm dòng: chọn một Type rồi tick nhiều tên cùng lúc, thay vì thêm từng dòng
        /// trống rồi gõ lại từ đầu.
        /// </summary>
        private void AddRow()
        {
            var dialog = new AddRowsWindow(TypeNames, _catalog) { Owner = Application.Current?.MainWindow };
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

        public void RemoveRows(IEnumerable<ComponentRow> rows)
        {
            foreach (var r in rows.ToList()) Rows.Remove(r);
        }

        /// <summary>Chèn bản sao ngay dưới dòng gốc, tiện khi thêm nhiều component cùng loại.</summary>
        public void DuplicateRows(IEnumerable<ComponentRow> rows)
        {
            foreach (var r in rows.ToList())
            {
                var at = Rows.IndexOf(r);
                if (at < 0) continue;
                Rows.Insert(at + 1, r.Clone());
            }
        }

        // ---------- resolve + run ----------

        private async Task<bool> ResolveAsync()
        {
            var targets = Rows.Where(r => r.Include).ToList();
            if (targets.Count == 0) { Warn("Không có dòng nào được chọn."); return false; }

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
            catch (OperationCanceledException) { Warn("Đã hủy."); }
            catch (Exception ex) { Error(ex.Message); }
            finally
            {
                IsBusy = false;
                Status = "Đã kết nối";
            }

            if (bad == 0) Good($"Tìm thấy đủ {ok}/{targets.Count} component.");
            else Warn($"Tìm thấy {ok}/{targets.Count}, còn {bad} dòng lỗi - sửa rồi chạy lại.");
            return bad == 0;
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

            if (!await ResolveAsync())
            {
                var answer = MessageBox.Show(
                    "Có dòng chưa tìm được component. Vẫn tiếp tục với những dòng hợp lệ?",
                    "Còn lỗi", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            var valid = rows.Where(r => r.IsResolved).ToList();
            if (valid.Count == 0) { Warn("Không có dòng hợp lệ để add."); return; }

            if (!dryRun)
            {
                var confirm = MessageBox.Show(
                    $"Add {valid.Count} component vào {selectedSolutions.Count} solution:\n\n" +
                    string.Join("\n", selectedSolutions.Select(s => "  - " + s.UniqueName)) +
                    "\n\nTiếp tục?",
                    "Xác nhận", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) return;
            }

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

                            if (present && !row.IncludeAll)
                            {
                                already++;
                                notes[row].Add($"{solution.UniqueName}: đã có");
                                if (row.State != RowState.Added) row.State = RowState.AlreadyIn;
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
                                bool? subcomponents = _catalog != null && row.ComponentType == _catalog.EntityComponentType
                                    ? !row.IncludeAll
                                    : (bool?)null;

                                await _solutions.AddAsync(solution.UniqueName, row.ObjectId, row.ComponentType,
                                    subcomponents, AddRequiredComponents, _cts.Token);
                                added++;
                                var what = present ? "đã cập nhật (include all)" : "đã add";
                                notes[row].Add($"{solution.UniqueName}: {what}");
                                row.State = RowState.Added;
                                Good($"  [{what}] {row.Type} {row.Name}");
                            }


                            await CleanupArchivalAsync(solution, solutionId, row, dryRun, notes[row]);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failed++;
                            notes[row].Add($"{solution.UniqueName}: LỖI {ex.Message}");
                            row.State = RowState.Failed;
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
                _settings.AddRequiredComponents = AddRequiredComponents;
                _settings.CleanupArchival = CleanupArchival;
                SaveSettings();
            }

            var verb = dryRun ? "sẽ add" : "đã add";
            var summary = $"XONG - {verb}: {added}, đã có sẵn: {already}, lỗi: {failed}.";
            if (failed > 0) Error(summary); else Good(summary);
        }

        /// <summary>
        /// Dataverse tự kéo bản ghi MetadataForArchival (metadata retention) của table vào solution,
        /// tạo ra một dòng thứ hai cùng tên. Gỡ những bản ghi chưa bật retention để solution hotfix
        /// chỉ chứa đúng thứ đã khai trong CSV. Mọi bước đều ghi log để lần chạy không gỡ được gì
        /// vẫn nói rõ lý do.
        /// </summary>
        private async Task CleanupArchivalAsync(SolutionTarget solution, string solutionId,
            ComponentRow row, bool dryRun, List<string> note)
        {
            if (!CleanupArchival || _catalog == null) return;
            if (row.ComponentType != _catalog.EntityComponentType) return;

            if (row.IncludeAll)
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
