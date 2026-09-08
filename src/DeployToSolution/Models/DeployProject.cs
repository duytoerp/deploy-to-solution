using System;
using System.Collections.Generic;

namespace DeployToSolution.Models
{
    /// <summary>
    /// Một đợt deploy: danh sách component của riêng đợt đó cộng các solution đích của nó.
    /// Mỗi hotfix là một dự án — mở lại tuần sau là có nguyên danh sách cũ, không phải dựng lại
    /// từ Excel.
    /// </summary>
    public class DeployProject
    {
        public string Name { get; set; } = "";

        /// <summary>Ghi chú tự do: số ticket, người yêu cầu, ngày hẹn deploy...</summary>
        public string Note { get; set; } = "";

        public string EnvironmentUrl { get; set; } = "";

        /// <summary>Unique name của các solution đích.</summary>
        public List<string> Solutions { get; set; } = new List<string>();

        public List<ProjectRow> Rows { get; set; } = new List<ProjectRow>();

        public bool AddRequiredComponents { get; set; }
        public bool CleanupArchival { get; set; } = true;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Lần chạy thật gần nhất - để biết dự án nào đã deploy, dự án nào còn dang dở.</summary>
        public DateTime? LastDeployedUtc { get; set; }

        public string LastResult { get; set; } = "";

        /// <summary>
        /// Dòng mô tả gọn cho danh sách dự án. Tính ra từ dữ liệu nên không ghi xuống file —
        /// ghi vào rồi thì lần sửa tay tiếp theo sẽ thấy một con số cũ mâu thuẫn với nội dung.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Summary
        {
            get
            {
                var when = LastDeployedUtc.HasValue
                    ? $"deploy {LastDeployedUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}"
                    : "chưa deploy";
                return $"{Rows.Count} component · {Solutions.Count} solution · {when}";
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>Một dòng component đã lược bỏ trạng thái chạy - chỉ giữ thứ người dùng khai.</summary>
    public class ProjectRow
    {
        public bool Include { get; set; } = true;
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IncludeAll { get; set; }
    }
}
