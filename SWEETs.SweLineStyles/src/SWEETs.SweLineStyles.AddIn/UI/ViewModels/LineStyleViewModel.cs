using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using SWEETs.SweLineStyles.AddIn.Core;

namespace SWEETs.SweLineStyles.AddIn.UI.ViewModels
{
    /// <summary>One editable pattern element. Numeric fields are text so partial input ("0.", "-") can be typed.</summary>
    public sealed class PatternElementViewModel : ObservableObject
    {
        public static PatternKind[] Kinds { get; } = { PatternKind.Dash, PatternKind.Gap, PatternKind.Dot, PatternKind.Shape, PatternKind.Text };

        private readonly Action _changed;
        private readonly PatternElement _model;   // keeps parameter order and key case of the original element

        public PatternElementViewModel(PatternElement model, Action changed)
        {
            _model = model.Clone();
            _changed = changed;
            _kind = model.Kind;
            _length = model.IsComplex || model.Kind == PatternKind.Dot ? "" : LinePattern.Num(model.Length);
            _content = model.Content;
            _source = model.Source;
            _scale = Opt(model.Get("S"));
            _rotation = Opt(model.Get("A") ?? model.Get("R"));
            _absolute = model.RotationAbsolute;
            _x = Opt(model.Get("X"));
            _y = Opt(model.Get("Y"));
        }

        private static string Opt(double? v) => v.HasValue ? LinePattern.Num(v.Value) : "";

        private PatternKind _kind;
        public PatternKind Kind
        {
            get => _kind;
            set
            {
                if (!Set(ref _kind, value)) return;
                if ((value == PatternKind.Dash || value == PatternKind.Gap) && string.IsNullOrWhiteSpace(_length)) { _length = ".25"; Raise(nameof(Length)); }
                if (value == PatternKind.Shape && string.IsNullOrWhiteSpace(_source)) { _source = "ltypeshp.shx"; Raise(nameof(Source)); }
                if (value == PatternKind.Text && (string.IsNullOrWhiteSpace(_source) || _source.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))) { _source = "STANDARD"; Raise(nameof(Source)); }
                if ((value == PatternKind.Shape || value == PatternKind.Text) && string.IsNullOrWhiteSpace(_scale)) { _scale = ".1"; Raise(nameof(Scale)); }
                Raise(nameof(IsLength)); Raise(nameof(IsComplex)); Raise(nameof(IsShape)); Raise(nameof(IsText));
                _changed();
            }
        }

        public bool IsLength => Kind == PatternKind.Dash || Kind == PatternKind.Gap;
        public bool IsComplex => Kind == PatternKind.Shape || Kind == PatternKind.Text;
        public bool IsShape => Kind == PatternKind.Shape;
        public bool IsText => Kind == PatternKind.Text;

        private string _length, _content, _source, _scale, _rotation, _x, _y;
        private bool _absolute;
        public string Length { get => _length; set { if (Set(ref _length, value)) _changed(); } }
        public string Content { get => _content; set { if (Set(ref _content, value)) _changed(); } }
        public string Source { get => _source; set { if (Set(ref _source, value)) _changed(); } }
        public string Scale { get => _scale; set { if (Set(ref _scale, value)) _changed(); } }
        public string Rotation { get => _rotation; set { if (Set(ref _rotation, value)) _changed(); } }
        public bool Absolute { get => _absolute; set { if (Set(ref _absolute, value)) _changed(); } }
        public string X { get => _x; set { if (Set(ref _x, value)) _changed(); } }
        public string Y { get => _y; set { if (Set(ref _y, value)) _changed(); } }

        private int _index;
        public int Index { get => _index; set => Set(ref _index, value); }

        /// <summary>Builds the element; returns null and an error when a number cannot be read.</summary>
        public PatternElement ToElement(out string error)
        {
            error = null;
            var e = _model.Clone();
            e.Kind = Kind;
            if (IsLength)
            {
                if (!TryNum(Length, out var len) || len < 0) { error = $"Element {Index}: length '{Length}' is not a positive number."; return null; }
                e.Length = Math.Abs(len);
                e.Params.Clear(); e.Content = ""; e.Source = "";
                return e;
            }
            e.Length = 0;
            if (!IsComplex) { e.Params.Clear(); e.Content = ""; e.Source = ""; return e; }

            e.Content = (Content ?? "").Trim();
            e.Source = (Source ?? "").Trim();
            if (!SetOpt(e, "S", Scale, ref error) | !SetOpt(e, "X", X, ref error) | !SetOpt(e, "Y", Y, ref error)) return null;
            // Rotation: A (absolute) or R (relative to the line). Keep whichever the original used when unchanged.
            if (string.IsNullOrWhiteSpace(Rotation)) { e.Set("A", null); e.Set("R", null); }
            else if (!TryNum(Rotation, out var rot)) { error = $"Element {Index}: rotation '{Rotation}' is not a number."; return null; }
            else if (Absolute) { e.Set("R", null); e.Set("A", rot); }
            else { e.Set("A", null); e.Set("R", rot); }
            return e;
        }

        private bool SetOpt(PatternElement e, string key, string text, ref string error)
        {
            if (string.IsNullOrWhiteSpace(text)) { e.Set(key, null); return true; }
            if (!TryNum(text, out var v)) { error = error ?? $"Element {Index}: {key} '{text}' is not a number."; return false; }
            e.Set(key, v);
            return true;
        }

        internal static bool TryNum(string s, out double v) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    public sealed class DescriptionRow : ObservableObject
    {
        private readonly Action _changed;
        public DescriptionRow(string code, string language, string text, Action changed) { Code = code; Language = language; _text = text ?? ""; _changed = changed; }
        public string Code { get; }
        public string Language { get; }
        private string _text;
        public string Text { get => _text; set { if (Set(ref _text, value ?? "")) _changed(); } }
    }

    /// <summary>A line style being edited. The raw pattern text and the element list are kept in sync both ways.</summary>
    public sealed class LineStyleViewModel : ObservableObject
    {
        private readonly LineStyle _original;      // null for a style created in this session
        private bool _syncing;

        public event Action Changed;

        public LineStyleViewModel(LineStyle model, bool isNew)
        {
            _original = isNew ? null : model.Clone();
            _name = model.Name;
            _ascii = model.Ascii;
            _pattern = model.Pattern;
            foreach (var (code, lang) in Languages.All)
                Descriptions.Add(new DescriptionRow(code, lang, model.Description(code), OnChanged));
            foreach (var extra in model.Descriptions.Where(d => Languages.Order(d.Key) == int.MaxValue))
                Descriptions.Add(new DescriptionRow(extra.Key, extra.Key, extra.Value, OnChanged));
            RebuildElements();

            AddDash = new RelayCommand(() => AddElement(PatternElement.Dash(.25)));
            AddGap = new RelayCommand(() => AddElement(PatternElement.Gap(.125)));
            AddDot = new RelayCommand(() => AddElement(PatternElement.Dot()));
            AddShape = new RelayCommand(() => AddElement(new PatternElement { Kind = PatternKind.Shape, Content = DefaultShape?.Invoke() ?? "", Source = "ltypeshp.shx", Params = { new PatternParam("S", .1) } }));
            AddText = new RelayCommand(() => AddElement(new PatternElement { Kind = PatternKind.Text, Content = "TXT", Source = "STANDARD", Params = { new PatternParam("S", .1), new PatternParam("R", 0), new PatternParam("X", -.1), new PatternParam("Y", -.05) } }));
            MoveUp = new RelayCommand(p => Move(p as PatternElementViewModel, -1), p => p is PatternElementViewModel e && Elements.IndexOf(e) > 0);
            MoveDown = new RelayCommand(p => Move(p as PatternElementViewModel, 1), p => p is PatternElementViewModel e && Elements.IndexOf(e) < Elements.Count - 1);
            Remove = new RelayCommand(p => { if (p is PatternElementViewModel e) { Elements.Remove(e); ElementsEdited(); } });
            GenerateAscii = new RelayCommand(() => { if (TryElements(out var list, out _)) Ascii = LinePattern.ToAscii(list); });
            FillDescriptions = new RelayCommand(() =>
            {
                var en = Descriptions.FirstOrDefault(d => d.Code == "en")?.Text;
                if (string.IsNullOrWhiteSpace(en)) return;
                foreach (var d in Descriptions.Where(d => string.IsNullOrWhiteSpace(d.Text))) d.Text = en;
            });
        }

        /// <summary>Supplies the first shape name for "+ Shape".</summary>
        public Func<string> DefaultShape { get; set; }

        public bool IsNew => _original == null;
        public string OriginalName => _original?.Name;

        private string _name, _ascii, _pattern, _patternError;
        public string Name { get => _name; set { if (Set(ref _name, value)) OnChanged(); } }
        public string Ascii { get => _ascii; set { if (Set(ref _ascii, value)) OnChanged(); } }

        /// <summary>Raw pattern text as stored in linestyle.xlin.</summary>
        public string Pattern
        {
            get => _pattern;
            set
            {
                if (!Set(ref _pattern, value)) return;
                if (!_syncing) RebuildElements();
                OnChanged();
            }
        }

        public string PatternError { get => _patternError; private set => Set(ref _patternError, value); }

        public ObservableCollection<PatternElementViewModel> Elements { get; } = new ObservableCollection<PatternElementViewModel>();
        public ObservableCollection<DescriptionRow> Descriptions { get; } = new ObservableCollection<DescriptionRow>();

        public string English => Descriptions.FirstOrDefault(d => d.Code == "en")?.Text ?? "";
        public int LanguageCount => Descriptions.Count(d => !string.IsNullOrWhiteSpace(d.Text));
        public string Kind { get { try { return LinePattern.Classify(LinePattern.Parse(Pattern)); } catch (FormatException) { return "Invalid"; } } }
        public string PeriodText
        {
            get
            {
                try { return "Pattern length " + LinePattern.Num(LinePattern.Period(LinePattern.Parse(Pattern))) + " units"; }
                catch (FormatException) { return ""; }
            }
        }

        public bool IsModified => _original == null || !ToModel().SameContent(_original);

        public ICommand AddDash { get; }
        public ICommand AddGap { get; }
        public ICommand AddDot { get; }
        public ICommand AddShape { get; }
        public ICommand AddText { get; }
        public ICommand MoveUp { get; }
        public ICommand MoveDown { get; }
        public ICommand Remove { get; }
        public ICommand GenerateAscii { get; }
        public ICommand FillDescriptions { get; }

        private void RebuildElements()
        {
            try
            {
                var list = LinePattern.Parse(Pattern);
                Elements.Clear();
                foreach (var e in list) Elements.Add(new PatternElementViewModel(e, ElementsEdited));
                Renumber();
                PatternError = null;
            }
            catch (FormatException ex) { PatternError = ex.Message; }
        }

        private void AddElement(PatternElement e)
        {
            Elements.Add(new PatternElementViewModel(e, ElementsEdited));
            ElementsEdited();
        }

        private void Move(PatternElementViewModel e, int by)
        {
            if (e == null) return;
            int i = Elements.IndexOf(e), j = i + by;
            if (i < 0 || j < 0 || j >= Elements.Count) return;
            Elements.Move(i, j);
            ElementsEdited();
        }

        private void Renumber() { for (int i = 0; i < Elements.Count; i++) Elements[i].Index = i + 1; }

        private bool TryElements(out List<PatternElement> list, out string error)
        {
            list = new List<PatternElement>();
            error = null;
            foreach (var vm in Elements)
            {
                var e = vm.ToElement(out error);
                if (e == null) return false;
                list.Add(e);
            }
            return true;
        }

        /// <summary>An element field changed: regenerate the raw pattern text from the elements.</summary>
        private void ElementsEdited()
        {
            Renumber();
            if (!TryElements(out var list, out var error)) { PatternError = error; OnChanged(); return; }
            PatternError = null;
            _syncing = true;
            try { Pattern = LinePattern.Format(list); }
            finally { _syncing = false; }
            OnChanged();
        }

        private void OnChanged()
        {
            Raise(nameof(IsModified)); Raise(nameof(English)); Raise(nameof(Kind)); Raise(nameof(PeriodText)); Raise(nameof(LanguageCount));
            Changed?.Invoke();
        }

        public LineStyle ToModel()
        {
            var m = new LineStyle { Name = (Name ?? "").Trim(), Pattern = Pattern ?? "", Ascii = Ascii ?? "" };
            foreach (var d in Descriptions.Where(d => !string.IsNullOrWhiteSpace(d.Text)))
                m.Descriptions.Add(new KeyValuePair<string, string>(d.Code, d.Text));
            return m;
        }

        /// <summary>Errors block saving; warnings are shown only.</summary>
        public void Validate(Func<string, string, bool> shapeExists, IEnumerable<string> otherNames, List<string> errors, List<string> warnings)
        {
            errors.AddRange(XlinFile.ValidateName(Name));
            if (otherNames.Any(n => string.Equals(n, (Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Another line style is already named '{Name}'.");
            if (PatternError != null) { errors.Add(PatternError); return; }
            List<PatternElement> list;
            try { list = LinePattern.Parse(Pattern); }
            catch (FormatException ex) { errors.Add(ex.Message); return; }
            LinePattern.Check(list, name => shapeExists(name, list.First(e => e.Kind == PatternKind.Shape && e.Content == name).Source), errors, warnings);
            if (string.IsNullOrWhiteSpace(English)) warnings.Add("No English description.");
            if (string.IsNullOrWhiteSpace(Ascii)) warnings.Add("The ASCII preview is empty (use Generate).");
        }
    }
}
