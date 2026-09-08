using System;
using System.Windows;

namespace DeployToSolution
{
    /// <summary>Hộp nhập một dòng chữ. WPF không có sẵn InputBox, mà kéo thêm thư viện chỉ vì việc này thì không đáng.</summary>
    public partial class PromptWindow : Window
    {
        private readonly Func<string, string> _validate;

        /// <param name="validate">Trả về thông báo lỗi nếu giá trị không hợp lệ, null nếu được.</param>
        public PromptWindow(string title, string label, string initial = "",
            string hint = null, Func<string, string> validate = null)
        {
            InitializeComponent();

            Title = title;
            Label.Text = label;
            Input.Text = initial ?? "";
            _validate = validate;

            Hint.Text = hint ?? "";
            Hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;

            Loaded += (s, e) => { Input.Focus(); Input.SelectAll(); };
        }

        public string Value => Input.Text.Trim();

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var problem = _validate?.Invoke(Value);
            if (problem != null)
            {
                MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                Input.Focus();
                return;
            }

            DialogResult = true;
        }

        /// <summary>Hỏi một dòng chữ; trả về null nếu người dùng hủy.</summary>
        public static string Ask(Window owner, string title, string label, string initial = "",
            string hint = null, Func<string, string> validate = null)
        {
            var dialog = new PromptWindow(title, label, initial, hint, validate) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Value : null;
        }
    }
}
