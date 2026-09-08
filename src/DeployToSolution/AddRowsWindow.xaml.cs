using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeployToSolution.Models;
using DeployToSolution.Services;

namespace DeployToSolution
{
    /// <summary>Một tên trong danh sách gợi ý, kèm ô tick.</summary>
    public class PickName : ObservableObject
    {
        private bool _checked;

        public string Name { get; set; }

        public bool Checked { get => _checked; set => Set(ref _checked, value); }
    }

    /// <summary>
    /// Popup "Thêm dòng": chọn một Type rồi tick nhiều tên cùng lúc, thay vì thêm từng dòng trống
    /// rồi gõ lại từ đầu. Với Column/View/Form/Chart/Key thì chọn bảng trước, danh sách sẽ chỉ
    /// còn component của bảng đó.
    /// </summary>
    public partial class AddRowsWindow : Window
    {
        /// <summary>Màu chữ gợi ý lúc bình thường, khớp với màu đặt trong XAML.</summary>
        private static readonly Brush CalmHint = new SolidColorBrush(Color.FromRgb(0x60, 0x5E, 0x5C));

        private readonly ComponentCatalog _catalog;               // null khi chưa kết nối
        private readonly Action<string> _log;
        private readonly List<PickName> _all = new List<PickName>();
        private readonly ObservableCollection<PickName> _shown = new ObservableCollection<PickName>();

        private bool _ready;          // chặn handler chạy trong lúc dựng cửa sổ
        private bool _suspend;        // chặn handler khi chính code này đang đổi ItemsSource
        private bool _tablesLoaded;
        private string _loadedKey;      // khoá của mẻ gợi ý đang giữ, để không nạp lại cùng một danh sách
        private string _loadedScopeKey; // loại + bảng đang xem, để biết lúc nào phải xoá ô Lọc
        private int _loadToken;         // bỏ kết quả của lần nạp đã cũ

        /// <summary>Các dòng người dùng bấm thêm. Bắn nhiều lần nếu bấm "Thêm &amp; chọn tiếp".</summary>
        public event Action<List<ComponentRow>> RowsRequested;

        public AddRowsWindow(IEnumerable<string> typeNames, ComponentCatalog catalog, Action<string> log = null)
        {
            InitializeComponent();
            _catalog = catalog;
            _log = log;

            // Chưa kết nối thì vẫn phải chọn được Type: dùng danh sách tên quen thuộc.
            var types = (typeNames ?? Enumerable.Empty<string>()).ToList();
            if (types.Count == 0) types = ComponentCatalog.FriendlyTypeNames.ToList();

            TypeCombo.ItemsSource = types;
            NameList.ItemsSource = _shown;

            _ready = true;
            TypeCombo.SelectedItem =
                types.FirstOrDefault(t => string.Equals(t, "Table", StringComparison.OrdinalIgnoreCase)) ?? types[0];
            UpdateCount();
        }

        /// <summary>Loại đang chọn. Đọc từ SelectedItem chứ không phải Text: ô Type không cho gõ.</summary>
        private string SelectedType => TypeCombo.SelectedItem as string ?? "";

        // ---------- nạp gợi ý theo Type (và bảng) ----------

        private async void Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _suspend) return;
            await RefreshAsync();
        }

        private async void Scope_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _suspend) return;
            await RefreshAsync();
        }

        /// <summary>
        /// ComboBox WPF đang đóng mà lăn chuột là âm thầm nhảy sang mục kế bên. Chọn PluginStep
        /// xong lăn chuột một nấc là thành PluginType mà không hề hay biết.
        /// </summary>
        private void Combo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ComboBox combo && !combo.IsDropDownOpen) e.Handled = true;
        }

        private async Task RefreshAsync()
        {
            var type = SelectedType.Trim();
            var scoped = ComponentCatalog.TryGetTableScope(type, out var separator);
            var table = (TableCombo.Text ?? "").Trim();

            // Ô bảng chỉ hiện với loại thuộc phạm vi một bảng; IncludeAll chỉ có nghĩa với Table.
            var scopeVisibility = scoped ? Visibility.Visible : Visibility.Collapsed;
            TableLabel.Visibility = scopeVisibility;
            TableCombo.Visibility = scopeVisibility;

            var isEntity = ComponentCatalog.CanonicalKey(type) == "entity";
            IncludeAllBox.IsEnabled = isEntity;
            if (!isEntity) IncludeAllBox.IsChecked = false;

            await LoadTablesAsync(scoped);

            // Đổi loại (hoặc đổi bảng) thì bộ lọc cũ không còn nghĩa gì - để nguyên là tưởng nạp thiếu.
            var scopeKey = $"{ComponentCatalog.CanonicalKey(type)}|{table.ToLowerInvariant()}";
            if (scopeKey != _loadedScopeKey)
            {
                _loadedScopeKey = scopeKey;
                if (FilterBox.Text.Length > 0)
                {
                    _suspend = true;
                    try { FilterBox.Clear(); }
                    finally { _suspend = false; }
                }
            }

            // Chữ trong ô Lọc là một phần của "tên đang gõ": với loại nhiều bản ghi, nó được
            // đẩy xuống server nên đổi chữ là phải nạp lại mẻ khác.
            var filter = (FilterBox.Text ?? "").Trim();
            var lookup = scoped ? table + separator + filter : filter;

            var key = _catalog != null ? _catalog.SuggestionKey(type, lookup) : scopeKey;
            if (key == _loadedKey) return;

            if (_catalog == null)
            {
                Settle(key, new List<string>(),
                    "Chưa kết nối môi trường nên không có gợi ý. Gõ tay tên ở ô bên dưới, mỗi dòng một tên.");
                return;
            }

            if (type.Length == 0)
            {
                Settle(key, new List<string>(), "Chọn Type để hiện danh sách tên.");
                return;
            }

            if (scoped && table.Length < 3)
            {
                Settle(key, new List<string>(),
                    $"Chọn bảng ở ô \"của bảng\" để hiện danh sách {type} của bảng đó.");
                return;
            }

            var token = ++_loadToken;
            Hint.Text = "Đang lấy danh sách từ môi trường...";

            List<string> names;
            try
            {
                names = await _catalog.SuggestNamesAsync(type, lookup, CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (token != _loadToken) return;
                Settle(key, new List<string>(),
                    $"Không lấy được gợi ý: {ex.Message} — vẫn gõ tay tên ở ô bên dưới được.");
                return;
            }

            if (token != _loadToken) return;   // người dùng đã đổi Type trong lúc chờ

            // Ghi lại đã đọc bảng nào, được bao nhiêu tên, ba tên đầu là gì. Nhìn nhật ký là đối
            // chiếu được ngay với màn hình solution trên web, khỏi phải đoán.
            _log?.Invoke($"Gợi ý {type} ({Source(type)}): {names.Count} tên" +
                         (names.Count > 0 ? ". Ví dụ: " + string.Join("  |  ", names.Take(3)) : "") +
                         (filter.Length > 0 ? $"   [lọc: {filter}]" : ""));

            Settle(key, names, Describe(type, names, filter));
        }

        /// <summary>Bảng Dataverse và mã componenttype của loại đang chọn, viết gọn cho một dòng.</summary>
        private string Source(string type)
        {
            var table = _catalog?.SourceTable(type);
            var code = _catalog?.MatchType(type)?.Value;
            if (string.IsNullOrEmpty(table)) return code == null ? "?" : $"componenttype {code}";
            return code == null ? table : $"{table}, componenttype {code}";
        }

        /// <summary>
        /// PluginType chứa tên CLASS plugin, mà class thường được đặt tên y hệt step
        /// (Hs.Vus.Plugins2.ClassTeacher.PostDeleteAsynchronous), nên rất dễ chọn nhầm.
        /// </summary>
        private static bool IsConfusable(string type) =>
            ComponentCatalog.CanonicalKey(type) == "plugintype";

        /// <summary>
        /// Nói thẳng khi danh sách bị cắt: môi trường có hàng chục nghìn step / web resource,
        /// không gõ gì thì cái hiện ra chỉ là mấy nghìn tên đầu theo alphabet.
        /// </summary>
        private string Describe(string type, List<string> names, string filter)
        {
            var min = ComponentCatalog.SearchMinLength;

            // Nói thẳng đang đọc bảng nào và mã componenttype: nhìn là biết ngay step hay plugin type.
            var from = $"  ·  đọc từ {Source(type)}";

            if (_catalog.SearchesOnServer(type) && filter.Length < min)
                return $"Môi trường có rất nhiều {type}, danh sách dưới đây mới là {names.Count} tên đầu " +
                       $"theo alphabet. Gõ ít nhất {min} ký tự vào ô Lọc (ví dụ Hs.Vus) để tìm thẳng trên môi trường.{from}";

            if (names.Count == 0)
                return (filter.Length > 0
                    ? $"Không có {type} nào khớp \"{filter}\"."
                    : "Môi trường không có component nào thuộc loại này.") + from;

            if (names.Count >= ComponentCatalog.SuggestLimit)
                return $"Danh sách bị cắt ở {ComponentCatalog.SuggestLimit} tên. Gõ thêm vào ô Lọc cho hẹp lại.{from}";

            return $"{names.Count} tên. Tick nhiều tên cùng lúc — mỗi tên sẽ thành một dòng riêng.{from}";
        }

        private void Settle(string key, List<string> names, string hint)
        {
            // Cảnh báo loại dễ nhầm phải hiện kể cả khi chưa kết nối - nó nói về lựa chọn Type,
            // không phải về dữ liệu lấy được.
            var warn = IsConfusable(SelectedType);

            _loadedKey = key;
            Hint.Text = warn
                ? "Plug-in type là CLASS plugin (componenttype 90), KHÔNG phải step, và không có trong " +
                  "cây Objects của Power Apps. Class hay được đặt tên y hệt step nên rất dễ nhầm — " +
                  "các dòng trong \"Plug-in steps\" trên web nằm ở Plug-in step.  " + hint
                : hint;
            Hint.Foreground = warn ? Brushes.Firebrick : CalmHint;
            Hint.FontWeight = warn ? FontWeights.SemiBold : FontWeights.Normal;
            SetNames(names);
        }

        private async Task LoadTablesAsync(bool needed)
        {
            if (!needed || _tablesLoaded || _catalog == null) return;

            List<string> tables;
            try
            {
                tables = await _catalog.SuggestNamesAsync("Table", "", CancellationToken.None);
            }
            catch
            {
                return;   // gợi ý bảng hỏng thì vẫn gõ tay tên bảng được
            }

            _suspend = true;
            try { TableCombo.ItemsSource = tables; }
            finally { _suspend = false; }
            _tablesLoaded = true;
        }

        private void SetNames(List<string> names)
        {
            foreach (var item in _all) item.PropertyChanged -= Pick_PropertyChanged;
            _all.Clear();

            foreach (var name in names)
            {
                var item = new PickName { Name = name };
                item.PropertyChanged += Pick_PropertyChanged;
                _all.Add(item);
            }

            ApplyFilter();
        }

        // ---------- lọc và tick ----------

        private async void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _suspend) return;

            ApplyFilter();          // lọc ngay trên mẻ đang có, khỏi phải chờ mạng
            await RefreshAsync();   // rồi nạp mẻ mới nếu chữ này cần tìm trên server
        }

        private void ApplyFilter()
        {
            var filter = (FilterBox.Text ?? "").Trim();

            _shown.Clear();
            foreach (var item in _all)
                if (filter.Length == 0 || item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    _shown.Add(item);

            ListInfo.Text = _all.Count == 0
                ? ""
                : _shown.Count == _all.Count ? $"{_all.Count} tên" : $"{_shown.Count}/{_all.Count} tên";

            UpdateCount();
        }

        private void Pick_PropertyChanged(object sender, PropertyChangedEventArgs e) => UpdateCount();

        /// <summary>Chỉ tick những dòng đang hiện, để lọc rồi tick cả cụm.</summary>
        private void CheckShown_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _shown) item.Checked = true;
        }

        private void UncheckAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _all) item.Checked = false;
        }

        private void ManualBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            UpdateCount();
        }

        // ---------- kết quả ----------

        /// <summary>Tên đã tick cộng tên gõ tay, bỏ trùng, giữ nguyên thứ tự.</summary>
        private List<string> Selected()
        {
            var names = _all.Where(i => i.Checked).Select(i => i.Name).ToList();

            var scoped = ComponentCatalog.TryGetTableScope(SelectedType, out var separator);
            var table = (TableCombo.Text ?? "").Trim();

            foreach (var line in ManualLines(ManualBox.Text))
            {
                // Gõ tay trong phạm vi một bảng thì tự gắn tiền tố bảng, trừ khi đã gõ đủ.
                names.Add(scoped && table.Length > 0 && line.IndexOf(separator) < 0
                    ? table + separator + line
                    : line);
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> ManualLines(string text) =>
            (text ?? "").Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim())
                // Dán thẳng từ báo cáo deploy được: bỏ dấu gạch đầu dòng, bỏ dòng ghi chú.
                .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l.Substring(2).Trim() : l)
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal));

        private void UpdateCount()
        {
            var count = Selected().Count;

            CountText.Text = count == 0 ? "Chưa chọn tên nào" : $"Đã chọn {count} tên";
            AddButton.Content = count == 0 ? "Thêm vào danh sách" : $"Thêm {count} dòng";
            AddButton.IsEnabled = count > 0;
            AddMoreButton.IsEnabled = count > 0;
        }

        private void AddClose_Click(object sender, RoutedEventArgs e)
        {
            if (Emit()) DialogResult = true;
        }

        private void AddMore_Click(object sender, RoutedEventArgs e)
        {
            if (!Emit()) return;

            foreach (var item in _all) item.Checked = false;
            ManualBox.Clear();
            TypeCombo.Focus();
        }

        private bool Emit()
        {
            var type = SelectedType.Trim();
            if (type.Length == 0)
            {
                MessageBox.Show(this, "Chọn Type trước.", "Thêm dòng",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var names = Selected();
            if (names.Count == 0)
            {
                MessageBox.Show(this, "Chưa chọn tên nào. Tick trong danh sách hoặc gõ tay ở ô bên dưới.",
                    "Thêm dòng", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var includeAll = IncludeAllBox.IsEnabled && IncludeAllBox.IsChecked == true;
            RowsRequested?.Invoke(names
                .Select(n => new ComponentRow { Type = type, Name = n, IncludeAll = includeAll })
                .ToList());
            return true;
        }
    }
}
