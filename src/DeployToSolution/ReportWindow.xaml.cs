using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using DeployToSolution.Services;
using Microsoft.Win32;

namespace DeployToSolution
{
    public partial class ReportWindow : Window
    {
        private readonly string _text;
        private readonly List<IEnumerable<string>> _grid;

        public ReportWindow(string text, List<IEnumerable<string>> grid = null)
        {
            InitializeComponent();
            _text = text ?? "";
            _grid = grid;
            ReportText.Text = _text;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_text);
                MessageBox.Show(this, "Đã copy toàn bộ báo cáo vào clipboard.", "Copy",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_grid == null || _grid.Count == 0)
            {
                MessageBox.Show(this, "Không có dữ liệu dạng bảng để xuất Excel.", "Lưu Excel",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Lưu báo cáo deploy",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = $"deploy-summary-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                ExcelWriter.Write(dlg.FileName, new[]
                {
                    new ExcelSheet
                    {
                        Name = "BaoCaoDeploy",
                        Rows = _grid,
                        ColumnWidths = new double[] { 34, 60 }
                    }
                });
                MessageBox.Show(this, "Đã lưu: " + dlg.FileName, "Lưu Excel",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Lưu Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Lưu báo cáo deploy",
                Filter = "Text (*.txt)|*.txt|Markdown (*.md)|*.md",
                FileName = $"deploy-report-{DateTime.Now:yyyyMMdd-HHmm}.txt"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dlg.FileName, _text, new UTF8Encoding(true));
                MessageBox.Show(this, "Đã lưu: " + dlg.FileName, "Lưu báo cáo",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Lưu báo cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
