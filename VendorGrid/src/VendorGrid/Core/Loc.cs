using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace VendorGrid.Core
{
    /// <summary>
    /// Localization. The English text is the key, so English always works as the fallback and the code stays readable.
    ///   * Code-generated text:  Loc.T("Search cancelled.")   Loc.F("Copied {0}", text)   Loc.N(n, "{0} listing", "{0} listings")
    ///   * XAML labels (Text, Content, Header, ToolTip, Title, AutomationProperties.Name) are translated automatically when the element loads.
    /// Translations live in Resources/Localization.tsv (embedded). A file named Localization.tsv next to VendorGrid.dll overrides it,
    /// so a translator can add or correct a language without rebuilding. First line = column headers ("en", "fr", "de" ...).
    /// </summary>
    public static class Loc
    {
        static Dictionary<string, string[]> _table;
        static string[] _columns = new string[0];
        static Dictionary<string, string> _current = new Dictionary<string, string>(StringComparer.Ordinal);
        static bool _hooked;
        static bool _active;

        public static string Language { get; private set; } = "en";
        public static string Source { get; private set; } = "the default";
        public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

        /// <summary>Supplies the language code of the open SOLIDWORKS Electrical project (set by the add-in). May return null.</summary>
        public static Func<string> HostLanguageProvider { get; set; }

        /// <summary>"en" plus every language column found in the translation file.</summary>
        public static IList<string> SupportedLanguages
        {
            get { EnsureLoaded(); return new[] { "en" }.Concat(_columns).ToList(); }
        }

        // ---------------------------------------------------------------------------------------------
        // Lookup
        // ---------------------------------------------------------------------------------------------

        public static string T(string english)
        {
            if (english == null) return "";
            string translated;
            return _current.TryGetValue(english, out translated) ? translated : english;
        }

        public static string F(string english, params object[] args)
        {
            try { return string.Format(Culture, T(english), args); }
            catch (FormatException) { return string.Format(Culture, english, args); }
        }

        /// <summary>Plural-aware: picks the singular or plural English form, translates it and formats with {0} = n, then any extra args.</summary>
        public static string N(int n, string one, string many, params object[] extra)
        {
            var args = new object[1 + extra.Length];
            args[0] = n;
            extra.CopyTo(args, 1);
            return F(n == 1 ? one : many, args);
        }

        // ---------------------------------------------------------------------------------------------
        // Language selection
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Chooses the language. setting = "" or "auto": follow SOLIDWORKS Electrical (language of the open project, then the host
        /// process, then Windows). Anything else is an explicit choice.
        /// </summary>
        public static void Initialize(string setting)
        {
            EnsureLoaded();
            var supported = new HashSet<string>(SupportedLanguages);

            string chosen = "en", source = "the default";
            var explicitSetting = !string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase);

            string hostRaw = null;
            try { var p = HostLanguageProvider; if (p != null) hostRaw = p(); } catch (Exception ex) { Log.Error("Host language lookup failed", ex); }
            var threadRaw = Thread.CurrentThread.CurrentUICulture.Name;
            var windowsRaw = CultureInfo.InstalledUICulture.Name;

            if (explicitSetting)
            {
                var s = Normalize(setting);
                chosen = s != null && supported.Contains(s) ? s : "en";
                source = "your selection";
            }
            else
            {
                var candidates = new[]
                {
                    new { Raw = hostRaw, Name = "the SOLIDWORKS Electrical project language" },
                    new { Raw = threadRaw, Name = "the host application's language" },
                    new { Raw = windowsRaw, Name = "the Windows display language" }
                };
                foreach (var c in candidates)
                {
                    var n = Normalize(c.Raw);
                    if (n == null) continue;
                    chosen = supported.Contains(n) ? n : "en";   // recognised but not translated yet: English, not a wrong guess
                    source = c.Name;
                    break;
                }
            }

            Language = chosen;
            Source = source;
            Culture = MakeCulture(chosen);
            _current = Build(chosen);
            _active = chosen != "en" && _current.Count > 0;
            Hook();

            Log.Info("Language: using '" + chosen + "' from " + source + " (setting='" + (setting ?? "") + "', project='" + (hostRaw ?? "") + "', host UI='" + threadRaw + "', Windows='" + windowsRaw + "')");
        }

        /// <summary>Maps a culture name ("fr-FR", "de_DE"), an ISO 639-2 code or a language name to a 2-letter code. Null when unrecognised.</summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.Trim().ToLowerInvariant();

            switch (t)
            {
                case "english": return "en";
                case "french": case "français": case "francais": return "fr";
                case "german": case "deutsch": return "de";
                case "spanish": case "español": case "espanol": case "castellano": return "es";
                case "italian": case "italiano": return "it";
                case "portuguese": case "português": case "portugues": return "pt";
                case "japanese": case "日本語": return "ja";
                case "chinese": case "中文": case "简体中文": case "繁體中文": return "zh";
                case "korean": case "한국어": return "ko";
                case "russian": case "русский": return "ru";
                case "polish": case "polski": return "pl";
                case "czech": case "čeština": case "cesky": return "cs";
                case "turkish": case "türkçe": case "turkce": return "tr";
                case "dutch": case "nederlands": return "nl";
            }

            var head = t.Split(new[] { '-', '_', ' ' })[0];
            switch (head)
            {
                case "eng": return "en";
                case "fra": case "fre": return "fr";
                case "deu": case "ger": return "de";
                case "spa": return "es";
                case "ita": return "it";
                case "por": return "pt";
                case "jpn": return "ja";
                case "zho": case "chi": return "zh";
                case "kor": return "ko";
                case "rus": return "ru";
                case "pol": return "pl";
                case "ces": case "cze": return "cs";
                case "tur": return "tr";
                case "nld": case "dut": return "nl";
            }
            return head.Length == 2 && head.All(char.IsLetter) ? head : null;
        }

        static CultureInfo MakeCulture(string lang)
        {
            string name;
            switch (lang)
            {
                case "en": name = "en-US"; break;
                case "zh": name = "zh-CN"; break;
                case "pt": name = "pt-BR"; break;
                default: name = lang; break;
            }
            try { return CultureInfo.GetCultureInfo(name); }
            catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en-US"); }
        }

        // ---------------------------------------------------------------------------------------------
        // Translation table
        // ---------------------------------------------------------------------------------------------

        static Dictionary<string, string> Build(string lang)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            int col = Array.IndexOf(_columns, lang);
            if (col < 0) return map;
            foreach (var kv in _table)
            {
                var v = kv.Value[col];
                if (!string.IsNullOrEmpty(v)) map[kv.Key] = v;
            }
            return map;
        }

        static void EnsureLoaded()
        {
            if (_table != null) return;
            _table = new Dictionary<string, string[]>(StringComparer.Ordinal);
            try
            {
                var text = ReadTable();
                if (text == null) return;
                using (var reader = new StringReader(text))
                {
                    var header = reader.ReadLine();
                    if (header == null) return;
                    _columns = header.Split('\t').Skip(1).Select(c => c.Trim().ToLowerInvariant()).ToArray();

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        var cells = line.Split('\t');
                        var values = new string[_columns.Length];
                        for (int i = 0; i < values.Length; i++) values[i] = i + 1 < cells.Length ? Unescape(cells[i + 1]) : "";
                        _table[Unescape(cells[0])] = values;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Could not read the translation table", ex); }
        }

        static string ReadTable()
        {
            var asm = Assembly.GetExecutingAssembly();
            try
            {
                var dir = Path.GetDirectoryName(asm.Location);
                var external = dir == null ? null : Path.Combine(dir, "Localization.tsv");
                if (external != null && File.Exists(external)) return File.ReadAllText(external, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("External translation file unreadable; using the built-in one", ex); }

            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Localization.tsv", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // XAML: translate literal labels as elements load (templates included)
        // ---------------------------------------------------------------------------------------------

        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        }

        static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_active) return;
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            // Inside SWEETs every tool shares the SOLIDWORKS Electrical process and its WPF class handlers: translate only
            // elements in VendorGrid's own windows, never the other SWEETs tools' windows.
            var window = fe as Window ?? Window.GetWindow(fe);
            if (window == null || window.GetType().Assembly != typeof(Loc).Assembly) return;
            try { Translate(fe); }
            catch (Exception ex) { Log.Error("Translating " + fe.GetType().Name + " failed", ex); }
        }

        static bool Bound(DependencyObject d, DependencyProperty p) { return BindingOperations.IsDataBound(d, p); }

        static void Translate(FrameworkElement fe)
        {
            var tb = fe as TextBlock;
            if (tb != null && !Bound(tb, TextBlock.TextProperty))
                foreach (var run in Runs(tb.Inlines))
                    if (!Bound(run, Run.TextProperty) && !string.IsNullOrEmpty(run.Text))
                    {
                        var t = T(run.Text);
                        if (t != run.Text) run.Text = t;
                    }

            var window = fe as Window;
            if (window != null && !Bound(window, Window.TitleProperty)) window.Title = T(window.Title);

            var header = fe as HeaderedContentControl;
            if (header != null && header.Header is string && !Bound(header, HeaderedContentControl.HeaderProperty))
                header.Header = T((string)header.Header);

            var content = fe as ContentControl;
            if (content != null && content.Content is string && !Bound(content, ContentControl.ContentProperty))
                content.Content = T((string)content.Content);

            var tip = fe.ToolTip as string;
            if (tip != null && !Bound(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = T(tip);

            var name = AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(name) && !Bound(fe, AutomationProperties.NameProperty))
            {
                var t = T(name);
                if (t != name) AutomationProperties.SetName(fe, t);
            }
        }

        static IEnumerable<Run> Runs(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                var run = inline as Run;
                if (run != null) { yield return run; continue; }
                var span = inline as Span;
                if (span != null) foreach (var inner in Runs(span.Inlines)) yield return inner;
            }
        }
    }
}
