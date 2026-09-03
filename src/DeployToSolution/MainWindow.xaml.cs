using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

            Closing += (s, e) => _vm.SaveSettings();
        }

        private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
            => _vm.ClientSecret = ((PasswordBox)sender).Password;

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = RowsGrid.SelectedItems.Cast<ComponentRow>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Chọn dòng cần xóa trước.", "Xóa dòng",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _vm.RemoveRows(selected);
        }
    }
}
