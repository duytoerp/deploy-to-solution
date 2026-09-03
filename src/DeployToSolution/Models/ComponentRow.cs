using System;
using System.Windows.Media;
using DeployToSolution.Models;

namespace DeployToSolution.Models
{
    /// <summary>One line of the components CSV: what to push into every selected solution.</summary>
    public class ComponentRow : ObservableObject
    {
        private bool _include = true;
        private string _type = "";
        private string _name = "";
        private bool _includeAll;
        private string _objectId;
        private int _componentType = -1;
        private RowState _state = RowState.Pending;
        private string _message = "";

        public bool Include { get => _include; set => Set(ref _include, value); }

        public string Type
        {
            get => _type;
            set { if (Set(ref _type, value)) Reset(); }
        }

        public string Name
        {
            get => _name;
            set { if (Set(ref _name, value)) Reset(); }
        }

        /// <summary>Entity rows only: add every column/form/view/relationship instead of the table alone.</summary>
        public bool IncludeAll { get => _includeAll; set => Set(ref _includeAll, value); }

        public string ObjectId { get => _objectId; set => Set(ref _objectId, value); }

        /// <summary>Tên thật khớp được lúc resolve (với Table là logical name) - dùng để dò component phụ.</summary>
        public string MatchedName { get; set; }

        public int ComponentType { get => _componentType; set => Set(ref _componentType, value); }

        public RowState State
        {
            get => _state;
            set { if (Set(ref _state, value)) { Raise(nameof(StateText)); Raise(nameof(StateBrush)); } }
        }

        public string Message { get => _message; set => Set(ref _message, value); }

        public string StateText => State switch
        {
            RowState.Pending => "",
            RowState.Resolved => "Đã tìm thấy",
            RowState.Added => "Đã add",
            RowState.AlreadyIn => "Đã có sẵn",
            RowState.Ambiguous => "Trùng tên",
            RowState.NotFound => "Không tìm thấy",
            RowState.Failed => "Lỗi",
            RowState.Skipped => "Bỏ qua",
            _ => State.ToString()
        };

        public Brush StateBrush => State switch
        {
            RowState.Added => Brushes.SeaGreen,
            RowState.Resolved => Brushes.SteelBlue,
            RowState.AlreadyIn => Brushes.Gray,
            RowState.Skipped => Brushes.Gray,
            RowState.NotFound => Brushes.Crimson,
            RowState.Ambiguous => Brushes.DarkOrange,
            RowState.Failed => Brushes.Crimson,
            _ => Brushes.Black
        };

        public bool IsResolved => !string.IsNullOrEmpty(ObjectId) && ComponentType >= 0;

        private void Reset()
        {
            ObjectId = null;
            MatchedName = null;
            ComponentType = -1;
            State = RowState.Pending;
            Message = "";
        }

        public ComponentRow Clone() => new ComponentRow
        {
            Include = Include, Type = Type, Name = Name, IncludeAll = IncludeAll
        };
    }
}
