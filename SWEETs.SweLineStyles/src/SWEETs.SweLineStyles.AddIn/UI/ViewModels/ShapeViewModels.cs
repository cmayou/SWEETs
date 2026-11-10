using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using SWEETs.SweLineStyles.AddIn.Core;
using SWEETs.SweLineStyles.AddIn.UI.Controls;

namespace SWEETs.SweLineStyles.AddIn.UI.ViewModels
{
    /// <summary>A shape of ltypeshp.shp as listed on the Shapes tab.</summary>
    public sealed class ShapeViewModel : ObservableObject
    {
        private readonly ShapeDefinition _original;   // null for a shape created in this session

        public ShapeViewModel(ShapeDefinition model, bool isNew)
        {
            _original = isNew ? null : model.Clone();
            Model = model.Clone();
            Decode();
        }

        public ShapeDefinition Model { get; private set; }
        public bool IsNew => _original == null;
        public string OriginalName => _original?.Name;

        public string Name => Model.Name;
        public int Number => Model.Number;
        public int ByteCount => Model.Bytes.Length;
        public string Summary => $"#{Number} · {ByteCount} bytes";
        public string Specification => string.Join(",", ShpFile.FormatGroups(Model.Bytes));

        private IList<ShapePrimitive> _primitives;
        public IList<ShapePrimitive> Primitives { get => _primitives; private set => Set(ref _primitives, value); }

        private string _error;
        public string Error { get => _error; private set => Set(ref _error, value); }

        private List<string> _usedBy = new List<string>();
        public List<string> UsedBy { get => _usedBy; set { Set(ref _usedBy, value); Raise(nameof(UsedByText)); Raise(nameof(IsUsed)); } }
        public bool IsUsed => UsedBy.Count > 0;
        public string UsedByText => UsedBy.Count == 0 ? "Not used by any line style" : UsedBy.Count == 1 ? "Used by " + UsedBy[0] : $"Used by {UsedBy.Count} line styles";

        public bool IsModified => _original == null || !Model.SameContent(_original);

        public void Update(ShapeDefinition model)
        {
            Model = model.Clone();
            // The original .shp text is kept only while number, name and bytes are unchanged.
            Model.SourceText = _original != null && Model.SameContent(_original) ? _original.SourceText : null;
            Decode();
            RaiseAll();
        }

        private void Decode()
        {
            try { Primitives = ShapeInterpreter.Decode(Model.Bytes); Error = null; }
            catch (FormatException ex) { Primitives = new List<ShapePrimitive>(); Error = ex.Message; }
        }
    }

    public enum DxfPlacement { KeepOrigin, CenterOnLine }

    /// <summary>The shape editor dialog: grid drawing, DXF import, live compile and preview.</summary>
    public sealed class ShapeEditorViewModel : ObservableObject
    {
        public const string PreviewKey = "\u0001SHAPE-EDITOR-PREVIEW";

        private readonly ShapeDefinition _source;          // null when creating
        private readonly HashSet<string> _otherNames;
        private readonly HashSet<int> _otherNumbers;
        private readonly Stack<List<ShapePrimitive>> _undo = new Stack<List<ShapePrimitive>>();
        private List<ShapePrimitive> _last = new List<ShapePrimitive>();
        private bool _restoring;
        private bool _geometryEdited;

        public ShapeEditorViewModel(ShapeDefinition source, IEnumerable<ShapeDefinition> others, int divisor, int extent)
        {
            _source = source;
            var list = others.ToList();
            _otherNames = new HashSet<string>(list.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            _otherNumbers = new HashSet<int>(list.Select(s => s.Number));

            _divisor = Divisors.Contains(divisor) ? divisor : 4;
            _extent = FitExtent(extent);
            if (source != null)
            {
                _name = source.Name;
                _number = source.Number.ToString(CultureInfo.InvariantCulture);
                List<ShapePrimitive> prims;
                try { prims = ShapeInterpreter.Decode(source.Bytes); }
                catch (FormatException) { prims = new List<ShapePrimitive>(); }
                foreach (var p in prims) Primitives.Add(p);
                _divisor = Math.Max(divisor, ShapeEncoder.SuggestDivisor(prims));
                if (Bounds.Of(prims, out var x0, out var y0, out var x1, out var y1))
                    _extent = FitExtent(Math.Max(extent, (int)Math.Ceiling(new[] { -x0, -y0, x1, y1 }.Max()) + 1));
            }
            else
            {
                _name = NextName();
                int n = Enumerable.Range(130, 126).FirstOrDefault(k => !_otherNumbers.Contains(k));
                if (n == 0) n = Enumerable.Range(1, 129).FirstOrDefault(k => !_otherNumbers.Contains(k));
                _number = n.ToString(CultureInfo.InvariantCulture);
            }
            _last = Primitives.ToList();
            Primitives.CollectionChanged += (s, e) =>
            {
                if (!_restoring) { _undo.Push(_last); _last = Primitives.ToList(); _geometryEdited = true; }
                Recompile();
            };

            Undo = new RelayCommand(DoUndo, () => _undo.Count > 0);
            Clear = new RelayCommand(() => Primitives.Clear(), () => Primitives.Count > 0);
            SetTool = new RelayCommand(p => { if (p is string s && Enum.TryParse(s, out ShapeTool t)) Tool = t; });
            ApplyImport = new RelayCommand(DoApplyImport, () => _dxf != null);
            Recompile();
        }

        private string NextName()
        {
            for (int i = 1; ; i++)
            {
                var n = "SHAPE" + i.ToString(CultureInfo.InvariantCulture);
                if (!_otherNames.Contains(n)) return n;
            }
        }

        public string Title => _source == null ? "New shape" : "Edit shape " + _source.Name;

        public ObservableCollection<ShapePrimitive> Primitives { get; } = new ObservableCollection<ShapePrimitive>();

        private string _name, _number;
        public string Name { get => _name; set { if (Set(ref _name, (value ?? "").ToUpperInvariant())) Validate(); } }
        public string Number { get => _number; set { if (Set(ref _number, value)) Validate(); } }

        private int _divisor, _extent;
        public int Divisor { get => _divisor; set { if (Set(ref _divisor, Math.Max(1, Math.Min(64, value)))) { _geometryEdited = true; Recompile(); } } }
        public int Extent { get => _extent; set => Set(ref _extent, Math.Max(1, Math.Min(120, value))); }
        public int[] Divisors { get; } = { 1, 2, 4, 8, 16, 32, 64 };
        public int[] Extents { get; } = { 2, 4, 8, 16, 32, 64, 120 };

        /// <summary>Smallest drawing-area choice that holds <paramref name="need"/> units.</summary>
        private int FitExtent(int need) => Extents.FirstOrDefault(x => x >= need) is int e && e > 0 ? e : Extents[Extents.Length - 1];

        private ShapeTool _tool = ShapeTool.Line;
        public ShapeTool Tool { get => _tool; set => Set(ref _tool, value); }

        private ShapePrimitive _selected;
        public ShapePrimitive Selected { get => _selected; set => Set(ref _selected, value); }

        private string _cursorText = "";
        public string CursorText { get => _cursorText; set => Set(ref _cursorText, value); }

        private bool _showCompiled = true;
        public bool ShowCompiled { get => _showCompiled; set => Set(ref _showCompiled, value); }

        // ---- compiled result
        private byte[] _bytes = { 0 };
        public byte[] Bytes => _bytes;
        private IList<ShapePrimitive> _compiled;
        public IList<ShapePrimitive> Compiled { get => _compiled; private set => Set(ref _compiled, value); }
        public string Specification => string.Join(",", ShpFile.FormatGroups(_bytes));
        public string ByteSummary => $"{_bytes.Length} of {ShapeDefinition.MaxBytes} bytes";

        private string _notes = "";
        public string Notes { get => _notes; private set => Set(ref _notes, value); }

        // ---- preview on a sample line
        private string _previewScale = ".1";
        public string PreviewScale { get => _previewScale; set { if (Set(ref _previewScale, value)) Raise(nameof(PreviewPattern)); } }
        public string PreviewPattern => $".5,-.25,[{PreviewKey},ltypeshp.shx,s={(PatternElementViewModel.TryNum(PreviewScale, out var s) && s > 0 ? LinePattern.Num(s) : ".1")}],-.25";
        private int _previewRevision;
        public int PreviewRevision { get => _previewRevision; private set => Set(ref _previewRevision, value); }

        // ---- validation
        private string _error;
        public string Error { get => _error; private set { Set(ref _error, value); Raise(nameof(CanAccept)); } }
        public bool CanAccept => Error == null;

        private void Recompile()
        {
            if (_source != null && !_geometryEdited)
                _bytes = (byte[])_source.Bytes.Clone();     // untouched geometry keeps its original bytes exactly
            else
            {
                _bytes = ShapeEncoder.Encode(Primitives, Divisor, out var notes);
                Notes = string.Join(" ", notes);
            }
            try { Compiled = ShapeInterpreter.Decode(_bytes); }
            catch (FormatException ex) { Compiled = new List<ShapePrimitive>(); Notes = ex.Message; }
            ShapeCatalog.SetPreview(PreviewKey, Compiled);
            PreviewRevision = ShapeCatalog.Revision;
            Raise(nameof(Bytes)); Raise(nameof(Specification)); Raise(nameof(ByteSummary));
            Validate();
        }

        private void Validate()
        {
            var probe = Result();
            string err = probe.Validate().FirstOrDefault();
            if (err == null && !int.TryParse(Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) err = "Shape number must be a whole number.";
            if (err == null && _otherNames.Contains(probe.Name)) err = $"Another shape is already named {probe.Name}.";
            if (err == null && _otherNumbers.Contains(probe.Number)) err = $"Shape number {probe.Number} is already used.";
            if (err == null && Primitives.Count == 0 && (_source == null || _geometryEdited)) err = "Draw or import at least one element.";
            Error = err;
        }

        public ShapeDefinition Result()
        {
            int.TryParse(Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
            return new ShapeDefinition { Number = n, Name = (Name ?? "").Trim(), Bytes = (byte[])_bytes.Clone() };
        }

        private void DoUndo()
        {
            if (_undo.Count == 0) return;
            var prev = _undo.Pop();
            _restoring = true;
            try { Primitives.Clear(); foreach (var p in prev) Primitives.Add(p); }
            finally { _restoring = false; }
            _last = prev.ToList();
            Recompile();
        }

        public ICommand Undo { get; }
        public ICommand Clear { get; }
        public ICommand SetTool { get; }
        public ICommand ApplyImport { get; }

        // ---- DXF import
        private DxfReader _dxf;
        private string _dxfFile = "";
        public string DxfFile { get => _dxfFile; private set { Set(ref _dxfFile, value); Raise(nameof(HasDxf)); } }
        public bool HasDxf => _dxf != null;
        private string _dxfInfo = "";
        public string DxfInfo { get => _dxfInfo; private set => Set(ref _dxfInfo, value); }
        private string _dxfScale = "1";
        public string DxfScale { get => _dxfScale; set => Set(ref _dxfScale, value); }
        private DxfPlacement _dxfPlacement = DxfPlacement.CenterOnLine;
        public DxfPlacement DxfPlacement { get => _dxfPlacement; set => Set(ref _dxfPlacement, value); }

        public void LoadDxf(string path)
        {
            var r = DxfReader.Read(path);
            if (r.Primitives.Count == 0) throw new InvalidOperationException("No lines, arcs, circles or polylines were found in the DXF file. " + r.SkippedSummary);
            _dxf = r;
            DxfFile = System.IO.Path.GetFileName(path);
            Bounds.Of(r.Primitives, out var x0, out var y0, out var x1, out var y1);
            double size = Math.Max(x1 - x0, y1 - y0);
            // Default: fit the drawing to 2 shape units (a typical line-style symbol at S≈.1 is then 0.2 drawing units).
            DxfScale = size > 0 ? LinePattern.Num(2 / size) : "1";
            DxfInfo = $"{r.Primitives.Count} elements, {LinePattern.Num(x1 - x0)} × {LinePattern.Num(y1 - y0)} DXF units. {r.SkippedSummary}".Trim();
            DoApplyImport();
        }

        private void DoApplyImport()
        {
            if (_dxf == null) return;
            if (!PatternElementViewModel.TryNum(DxfScale, out var k) || k <= 0) { Notes = "DXF scale must be a positive number."; return; }
            Bounds.Of(_dxf.Primitives, out var x0, out var y0, out var x1, out var y1);
            var shift = DxfPlacement == DxfPlacement.CenterOnLine ? new Pt(-x0, -(y0 + y1) / 2) : new Pt(0, 0);
            Pt Map(Pt p) => (p + shift) * k;
            var mapped = _dxf.Primitives.Select(p => p.Transformed(Map, k)).ToList();
            Bounds.Of(mapped, out var mx0, out var my0, out var mx1, out var my1);
            Extent = FitExtent(Math.Max(Extent, (int)Math.Ceiling(new[] { -mx0, -my0, mx1, my1 }.Max()) + 1));
            Primitives.Clear();
            foreach (var p in mapped) Primitives.Add(p);
        }
    }
}
