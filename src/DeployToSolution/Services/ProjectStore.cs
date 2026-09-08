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
        public static List<DeployProject> LoadAll()
        {
            var list = new List<DeployProject>();
            try
            {
                if (!Directory.Exists(Dir)) return list;
                foreach (var file in Directory.GetFiles(Dir, "*.json"))
                {
                    var project = Read(file);
                    if (project != null) list.Add(project);
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

                // File sửa tay có thể thiếu Name; lấy tên file làm tên dự án còn hơn mất luôn file đó.
                if (string.IsNullOrWhiteSpace(project.Name))
                    project.Name = Path.GetFileNameWithoutExtension(file);

                return project;
            }
            catch { return null; }   // một file hỏng không được làm mất các dự án còn lại
        }

        public static void Save(DeployProject project)
        {
            Directory.CreateDirectory(Dir);
            project.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(FileFor(project.Name), JsonSerializer.Serialize(project, Opts));
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
