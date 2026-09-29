using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeployToSolution.Models;

namespace DeployToSolution.Services
{
    /// <summary>
    /// Mỗi dự án là một file JSON trong %APPDATA%\DeployToSolution\projects. Một file một dự án,
    /// đọc được bằng mắt — copy sang máy khác hay gửi cho đồng nghiệp là xong, không cần export.
    /// </summary>
    public static class ProjectStore
    {
        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeployToSolution", "projects");

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>Mới sửa gần nhất lên đầu: dự án đang làm dở thường là dự án cần mở lại.</summary>
        public static List<DeployProject> LoadAll() => LoadAll(out _);

        /// <param name="broken">Tên các file không đọc được, để báo cho người dùng thay vì lặng lẽ giấu đi.</param>
        public static List<DeployProject> LoadAll(out List<string> broken)
        {
            var list = new List<DeployProject>();
            broken = new List<string>();
            try
            {
                if (!Directory.Exists(Dir)) return list;
                foreach (var file in Directory.GetFiles(Dir, "*.json"))
                {
                    var project = Read(file);
                    if (project != null) list.Add(project);
                    else broken.Add(Path.GetFileName(file));
                }
            }
            catch { /* thư mục hỏng không được chặn app khởi động */ }

            return list.OrderByDescending(p => p.UpdatedUtc).ToList();
        }

        public static DeployProject Read(string file)
        {
            try
            {
                var project = JsonSerializer.Deserialize<DeployProject>(File.ReadAllText(file));
                if (project == null) return null;

                // File sửa tay hoặc do đồng nghiệp gửi có thể thiếu trường; "Rows": null mà để nguyên
                // là app văng ngay lúc khởi động.
                project.Rows = (project.Rows ?? new List<ProjectRow>()).Where(r => r != null).ToList();
                project.Solutions = (project.Solutions ?? new List<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                project.Note ??= "";
                project.EnvironmentUrl ??= "";
                project.LastResult ??= "";

                // Tên file mới là danh tính của dự án: file bị copy/đổi tên (ví dụ "A - Copy.json" mà bên
                // trong vẫn ghi Name "A") thì lấy theo tên file, không thì hai dự án cùng ghi vào A.json.
                if (string.IsNullOrWhiteSpace(project.Name) ||
                    !SamePath(FileFor(project.Name), file))
                    project.Name = Path.GetFileNameWithoutExtension(file);

                return project;
            }
            catch { return null; }   // một file hỏng không được làm mất các dự án còn lại
        }

        /// <summary>Ghi ra file tạm rồi mới thay file thật: tắt máy giữa chừng không để lại file JSON cụt.</summary>
        public static void Save(DeployProject project)
        {
            Directory.CreateDirectory(Dir);
            project.UpdatedUtc = DateTime.UtcNow;

            var target = FileFor(project.Name);
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(project, Opts));
            File.Move(temp, target, overwrite: true);
        }

        public static void Delete(string name)
        {
            try
            {
                var file = FileFor(name);
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        public static bool Exists(string name) => File.Exists(FileFor(name));

        public static string FileFor(string name) => Path.Combine(Dir, Sanitize(name) + ".json");

        /// <summary>
        /// Hai tên có trỏ cùng một file không. Windows không phân biệt hoa/thường, và "a/b" với "a_b"
        /// cùng ra a_b.json sau khi lọc ký tự.
        /// </summary>
        public static bool SameFile(string nameA, string nameB) => SamePath(FileFor(nameA), FileFor(nameB));

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>Tên dự án do người dùng đặt nên phải lọc ký tự không hợp lệ trước khi thành tên file.</summary>
        public static string Sanitize(string name)
        {
            var clean = (name ?? "").Trim();
            foreach (var bad in Path.GetInvalidFileNameChars()) clean = clean.Replace(bad, '_');
            clean = clean.Trim('.', ' ');
            return clean.Length == 0 ? "du-an" : clean;
        }
    }
}
