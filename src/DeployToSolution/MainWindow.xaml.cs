using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeployToSolution.Models;
using DeployToSolution.ViewModels;

namespace DeployToSolution
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            // Keep the newest log line in view without stealing focus from the grid.
            ((INotifyCollectionChanged)_vm.Log).CollectionChanged += (s, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Add) return;
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    if (LogList.Items.Count > 0)
                        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                }));
            };

            // Dòng mới thêm ở đầu: cuộn lên và chọn sẵn để gõ ngay.
            _vm.RowAdded += row =>
            {
                RowsGrid.ScrollIntoView(row);
                RowsGrid.SelectedItem = row;
                RowsGrid.CurrentCell = new DataGridCellInfo(row, RowsGrid.Columns[1]);
            };

            Closing += (s, e) =>
            {
                // Tự lưu dự án khi đóng: không bao giờ để mất danh sách vừa dựng vì quên bấm Lưu.
                _vm.SaveCurrentProject(quiet: true);
                _vm.SaveSettings();
            };
        }

        private void RowsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Row?.Item is not ComponentRow row) return;
            if (!string.Equals(e.Column?.Header as string, "Name", System.StringComparison.Ordinal)) return;

            // Nạp nền: người dùng vẫn gõ được ngay, danh sách hiện ra khi truy vấn xong.
            _ = _vm.LoadNameSuggestionsAsync(row);
        }

        private async void NameCombo_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not ComboBox combo || combo.DataContext is not ComponentRow row) return;

            var typed = combo.Text;
            await _vm.LoadNameSuggestionsAsync(row, typed);

            // Đổi ItemsSource có thể làm ComboBox tự viết lại ô text; giữ đúng thứ người dùng đang gõ.
            if (combo.Text != typed) combo.Text = typed;

            if (!combo.IsDropDownOpen && typed.Length >= 2 && row.NameSuggestions.Count > 0)
                combo.IsDropDownOpen = true;
        }

        private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
            => _vm.ClientSecret = ((PasswordBox)sender).Password;

        /// <summary>
        /// ComboBox WPF đang đóng mà lăn chuột là âm thầm nhảy sang mục kế bên - với ô Dự án thì
        /// đó là mở nhầm cả một danh sách component khác.
        /// </summary>
        private void Combo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ComboBox combo && !combo.IsDropDownOpen) e.Handled = true;
        }

        /// <summary>Chuột phải vào một dòng chưa được chọn thì chọn dòng đó, để menu tác động đúng chỗ.</summary>
        private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridRow row) return;
            if (row.IsSelected) return; // Đang chọn nhiều dòng thì giữ nguyên vùng chọn.

            RowsGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }

        private void RowsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;

            // Đang sửa trong ô thì Delete là xoá ký tự, không phải xoá dòng.
            if (e.OriginalSource is not DataGridCell && e.OriginalSource is not DataGridRow &&
                e.OriginalSource is not DataGrid) return;

            var selected = SelectedRows();
            if (selected.Count == 0) return;

            _vm.RemoveRows(selected);
            e.Handled = true;
        }

        private List<ComponentRow> SelectedRows() =>
            RowsGrid.SelectedItems.OfType<ComponentRow>().ToList();

        private bool RequireSelection(out List<ComponentRow> rows, string action)
        {
            rows = SelectedRows();
            if (rows.Count > 0) return true;

            MessageBox.Show($"Chọn dòng trước khi {action}.", "Chưa chọn dòng",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (RequireSelection(out var selected, "xóa")) _vm.RemoveRows(selected);
        }

        private void DuplicateSelected_Click(object sender, RoutedEventArgs e)
        {
            if (RequireSelection(out var selected, "nhân đôi")) _vm.DuplicateRows(selected);
        }

        private void CheckSelected_Click(object sender, RoutedEventArgs e) => SetInclude(true);

        private void UncheckSelected_Click(object sender, RoutedEventArgs e) => SetInclude(false);

        private void SetInclude(bool include)
        {
            foreach (var row in SelectedRows()) row.Include = include;
        }

        private void CopyName_Click(object sender, RoutedEventArgs e) =>
            CopyColumn(r => r.Name, "Name");

        private void CopyObjectId_Click(object sender, RoutedEventArgs e) =>
            CopyColumn(r => r.ObjectId, "ObjectId");

        private void CopyColumn(Func<ComponentRow, string> pick, string what)
        {
            var values = SelectedRows()
                .Select(pick)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (values.Count == 0)
            {
                MessageBox.Show($"Không có {what} nào để copy. " +
                                "Với ObjectId, chạy 'Kiểm tra tên' trước.",
                    "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try { Clipboard.SetText(string.Join(Environment.NewLine, values)); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Copy", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
