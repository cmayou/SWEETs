using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VendorGrid.Addin;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    /// <summary>A manufacturer part used in the SOLIDWORKS Electrical project, shown as a clickable tile.</summary>
    public sealed class ProjectPartVM
    {
        public ProjectPartVM(ProjectPart p)
        {
            Part = p;
        }

        public ProjectPart Part { get; private set; }
        public string Mpn { get { return Part.Reference; } }
        public string Manufacturer { get { return Part.Manufacturer; } }
        public string Description { get { return Part.Description; } }
        public bool HasQty { get { return Part.Quantity > 0; } }
        public int Quantity { get { return Part.Quantity; } }
        public string QtyText { get { return "×" + Money.Count(Part.Quantity); } }
        public string ToolTipText
        {
            get
            {
                var lines = new List<string> { Part.Reference };
                if (!string.IsNullOrEmpty(Part.Manufacturer)) lines.Add(Loc.F("Manufacturer: {0}", Part.Manufacturer));
                if (!string.IsNullOrEmpty(Part.Description)) lines.Add(Part.Description);
                if (!string.IsNullOrEmpty(Part.Supplier)) lines.Add(Loc.F("Supplier: {0}", Part.Supplier + (string.IsNullOrEmpty(Part.StockNumber) ? "" : " · " + Part.StockNumber)));
                if (Part.Quantity > 0) lines.Add(Loc.F("Used {0}× in the project", Part.Quantity));
                lines.Add(Loc.T("Click to search vendors for this part"));
                return string.Join("\n", lines);
            }
        }
    }

    /// <summary>
    /// Parts of the open SOLIDWORKS Electrical project(s). Follows the active project unless the user picks another open one.
    /// Reading happens on the UI thread because the Electrical COM objects belong to it.
    /// </summary>
    public sealed class ProjectVM : ObservableObject
    {
        readonly List<ProjectPartVM> _all = new List<ProjectPartVM>();
        ProjectRef _selected;
        bool _followActive = true;
        bool _loading;
        bool _expanded = true;
        string _filter = "";
        string _message = "";
        DateTime _lastLoadUtc = DateTime.MinValue;
        bool _hasProject;

        public ProjectVM()
        {
            RefreshCommand = new RelayCommand(_ => Reload(true));
            ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
            ChooseCommand = new RelayCommand(p => { var part = p as ProjectPartVM; var h = PartChosen; if (part != null && h != null) h(part); });
        }

        public ObservableCollection<ProjectRef> Projects { get; } = new ObservableCollection<ProjectRef>();
        public ObservableCollection<ProjectPartVM> Parts { get; } = new ObservableCollection<ProjectPartVM>();

        public RelayCommand RefreshCommand { get; private set; }
        public RelayCommand ToggleCommand { get; private set; }
        public RelayCommand ChooseCommand { get; private set; }

        /// <summary>Raised when the user clicks a part tile; the main view-model starts the search.</summary>
        public event Action<ProjectPartVM> PartChosen;

        public bool HostAvailable { get { return ProjectReader.IsConnected; } }
        public bool HasProject { get { return _hasProject; } private set { if (Set(ref _hasProject, value)) { OnPropertyChanged("NoProject"); OnPropertyChanged("ShowParts"); OnPropertyChanged("ShowEmpty"); } } }
        public bool NoProject { get { return !_hasProject; } }
        public bool HasMultipleProjects { get { return Projects.Count > 1; } }
        public bool IsExpanded { get { return _expanded; } set { if (Set(ref _expanded, value)) { OnPropertyChanged("ShowParts"); OnPropertyChanged("ShowEmpty"); OnPropertyChanged("ToggleGlyph"); } } }
        public string ToggleGlyph { get { return _expanded ? "▾" : "▸"; } }

        public bool ShowParts { get { return _hasProject && _expanded; } }
        public bool ShowEmpty { get { return !_hasProject && _expanded; } }

        public string Message { get { return _message; } private set { Set(ref _message, value); } }
        public string Title { get { return _selected == null ? Loc.T("SOLIDWORKS Electrical project") : _selected.Name; } }
        public string CountText
        {
            get
            {
                if (!_hasProject) return "";
                return Loc.N(_all.Count, "{0} part", "{0} parts");
            }
        }

        public string Filter
        {
            get { return _filter; }
            set { if (Set(ref _filter, value ?? "")) ApplyFilter(); }
        }

        public ProjectRef SelectedProject
        {
            get { return _selected; }
            set
            {
                if (_loading || value == null || ReferenceEquals(value, _selected)) return;
                _selected = value;
                _followActive = value.IsActive;
                OnPropertyChanged("SelectedProject");
                LoadParts(value.Id);
            }
        }

        /// <summary>Reload when the window is re-activated (the user may have switched project or edited parts in Electrical).</summary>
        public void RefreshIfStale()
        {
            if ((DateTime.UtcNow - _lastLoadUtc).TotalSeconds > 5) Reload(false);
        }

        public void Reload(bool force)
        {
            _lastLoadUtc = DateTime.UtcNow;
            _loading = true;
            try
            {
                if (!ProjectReader.IsConnected)
                {
                    SetEmpty(Loc.T("Running outside SOLIDWORKS Electrical. Enter a part number above to search."));
                    return;
                }

                var open = ProjectReader.ListOpen();
                var previousId = _selected == null ? 0 : _selected.Id;

                Projects.Clear();
                foreach (var p in open) Projects.Add(p);
                OnPropertyChanged("HasMultipleProjects");

                if (open.Count == 0)
                {
                    _selected = null;
                    OnPropertyChanged("SelectedProject");
                    OnPropertyChanged("Title");
                    SetEmpty(Loc.T("No SOLIDWORKS Electrical project is open. Open a project to list its parts here, or enter a part number above."));
                    return;
                }

                ProjectRef pick = null;
                if (!_followActive) pick = open.FirstOrDefault(p => p.Id == previousId);
                if (pick == null) { pick = open.FirstOrDefault(p => p.IsActive) ?? open[0]; _followActive = pick.IsActive; }
                _selected = pick;
                OnPropertyChanged("SelectedProject");
                OnPropertyChanged("Title");
            }
            finally { _loading = false; }

            if (_selected != null) LoadParts(_selected.Id);
        }

        void LoadParts(int projectId)
        {
            ProjectSnapshot snap;
            try { snap = ProjectReader.Read(projectId); }
            catch (Exception ex) { Log.Error("Project load failed", ex); SetEmpty(Loc.F("Could not read the project: {0}", ex.Message)); return; }

            _all.Clear();
            foreach (var p in snap.Parts.OrderBy(p => p.Manufacturer, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Reference, StringComparer.OrdinalIgnoreCase))
                _all.Add(new ProjectPartVM(p));

            HasProject = snap.Project != null;
            Message = snap.Project == null
                ? (snap.Warning ?? Loc.T("No SOLIDWORKS Electrical project is open."))
                : _all.Count == 0
                    ? Loc.T("This project has no manufacturer parts assigned yet.")
                    : (snap.QuantitiesComplete ? "" : Loc.T("Quantities could not be counted for this project. The quantity field is left unchanged."));
            if (!string.IsNullOrEmpty(snap.Warning) && snap.Project != null) Message = snap.Warning;
            OnPropertyChanged("Title");
            OnPropertyChanged("CountText");
            ApplyFilter();
            Log.Info("Project parts loaded: " + _all.Count + (snap.Project != null ? " from " + snap.Project.Name : ""));
        }

        void SetEmpty(string message)
        {
            _all.Clear();
            Parts.Clear();
            HasProject = false;
            Message = message;
            OnPropertyChanged("CountText");
            OnPropertyChanged("Title");
        }

        void ApplyFilter()
        {
            Parts.Clear();
            var f = (_filter ?? "").Trim();
            foreach (var p in _all)
            {
                if (f.Length == 0
                    || p.Mpn.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                    || p.Manufacturer.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                    || p.Description.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    Parts.Add(p);
            }
        }
    }
}
