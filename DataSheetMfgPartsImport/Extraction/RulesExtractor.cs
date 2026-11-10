using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataSheetMfgPartsImport.Model;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport.Extraction
{
    /// <summary>
    /// Offline extraction from the text of a PDF using label and unit patterns (English, German, French, Italian).
    /// It never guesses silently: values found more than once, and values inferred from layout, are flagged for review.
    /// </summary>
    internal sealed class RulesExtractor : IExtractor
    {
        const int MaxPages = 60;
        const int MaxVariants = 300;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        const RegexOptions Cs = RegexOptions.CultureInvariant;

        public Task<ExtractionResult> ExtractAsync(IList<string> files, AppSettings settings, CancellationToken ct)
        {
            return Task.Run(() => Run(files, ct), ct);
        }

        static ExtractionResult Run(IList<string> files, CancellationToken ct)
        {
            var result = new ExtractionResult();
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                    throw new ExtractionException("The offline rules engine reads PDFs with selectable text. For photos, switch to the local AI model in Settings.");
                if (!File.Exists(path)) throw new ExtractionException("File not found: " + path);

                var content = PdfText.Read(path, MaxPages);
                if (content.CharCount < 200)
                    throw new ExtractionException("This PDF has almost no selectable text (it may be a scan). Use a PDF with real text, or switch to the local AI model in Settings.");

                result.Parts.AddRange(Parse(content, path));
                result.PageCount += content.PageCount;
                result.HasTextLayer = true;
                result.Lines = content.Lines;
                if (result.Title.Length == 0)
                    result.Title = !string.IsNullOrWhiteSpace(content.Title) ? content.Title.Trim() : Path.GetFileNameWithoutExtension(path);
            }
            if (result.Parts.Count == 0) throw new ExtractionException("No part data was found in this PDF.");
            return result;
        }

        // ───────── value patterns ─────────

        const string N = @"\d+(?:[.,]\d+)?";
        const string R = N + @"(?:\s*(?:\.\.\.|…|–|—|-|/|to|bis|à)\s*" + N + ")?";

        static readonly Regex VoltRx = new Regex("(?<v>" + R + @")\s*V(?:olts?)?(?:\s?(?<t>AC|DC|ac|dc))?(?![A-Za-z])", Cs);
        static readonly Regex AmpRx = new Regex("(?<v>" + R + @")\s*A(?![A-Za-z0-9])", Cs);
        static readonly Regex PowerRx = new Regex("(?<v>" + N + @")\s*(?<u>kW|W|hp|HP)(?![A-Za-z])", Cs);
        static readonly Regex HzRx = new Regex("(?<v>" + R + @")\s*Hz", Cs);
        static readonly Regex MmRx = new Regex("(?<v>" + N + @")\s*(?<u>mm|cm|in)(?![A-Za-z])", Cs);
        static readonly Regex KgRx = new Regex("(?<v>" + N + @")\s*/?\s*(?<u>kg|g|lbs|lb)(?![A-Za-z])", Cs);
        static readonly Regex TempRx = new Regex(@"(?<a>[-+−]?\s?\d{1,3})\s*°?\s*(?:\.\.\.|…|–|—|-|to|bis|à)\s*(?<b>[-+]?\s?\d{1,3})\s*°\s?C", Ci);
        static readonly Regex TripleRx = new Regex("(?<a>" + N + @")\s*[x×X]\s*(?<b>" + N + @")\s*[x×X]\s*(?<c>" + N + @")\s*(?<u>mm|cm)", Cs);

        static string NormRange(string s)
        {
            s = Regex.Replace(s, @"\s*(?:\.\.\.|…|–|—|to|bis|à)\s*", "-", Ci);
            s = Regex.Replace(s, @"\s*([-/])\s*", "$1");
            return s.Replace(',', '.').Trim();
        }

        static double ParseNumber(string s)
        {
            double d;
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out d) ? d : double.NaN;
        }

        static string Fmt(double d)
        {
            return d.ToString("0.######", Inv);
        }

        static string Voltage(string rest)
        {
            var m = VoltRx.Match(rest);
            if (!m.Success) return null;
            return NormRange(m.Groups["v"].Value) + (m.Groups["t"].Success ? " " + m.Groups["t"].Value.ToUpperInvariant() : "");
        }

        static string Current(string rest)
        {
            var m = AmpRx.Match(rest);
            return m.Success ? NormRange(m.Groups["v"].Value) : null;
        }

        static string Power(string rest)
        {
            var m = PowerRx.Match(rest);
            if (!m.Success) return null;
            double v = ParseNumber(m.Groups["v"].Value);
            if (double.IsNaN(v)) return null;
            string u = m.Groups["u"].Value;
            if (u == "kW") v *= 1000;
            else if (u == "hp" || u == "HP") v *= 745.7;
            return Fmt(v);
        }

        static string Frequency(string rest)
        {
            var m = HzRx.Match(rest);
            return m.Success ? NormRange(m.Groups["v"].Value) : null;
        }

        static string Millimetres(string rest)
        {
            var m = MmRx.Match(rest);
            if (!m.Success) return null;
            double v = ParseNumber(m.Groups["v"].Value);
            if (double.IsNaN(v)) return null;
            string u = m.Groups["u"].Value;
            if (u == "cm") v *= 10;
            else if (u == "in") v *= 25.4;
            return Fmt(v);
        }

        static string Kilograms(string rest)
        {
            var m = KgRx.Match(rest);
            if (!m.Success) return null;
            double v = ParseNumber(m.Groups["v"].Value);
            if (double.IsNaN(v)) return null;
            string u = m.Groups["u"].Value;
            if (u == "g") v /= 1000.0;
            else if (u == "lb" || u == "lbs") v *= 0.45359237;
            return Fmt(v);
        }

        static string Temperature(string rest)
        {
            var m = TempRx.Match(rest);
            if (!m.Success) return null;
            string a = Regex.Replace(m.Groups["a"].Value.Replace('−', '-'), @"\s", "");
            string b = Regex.Replace(m.Groups["b"].Value, @"\s", "");
            if (!b.StartsWith("+", StringComparison.Ordinal) && !b.StartsWith("-", StringComparison.Ordinal)) b = "+" + b;
            return a + "..." + b;
        }

        // ───────── label patterns ─────────

        static Regex L(string pattern) { return new Regex(pattern, Ci); }

        static readonly Regex LblVoltage = L(@"(?:rated\s+(?:operational\s+|operating\s+|insulation\s+|supply\s+)?voltage|operating\s+voltage|supply\s+voltage|nominal\s+voltage|voltage\s+rating|Bemessungs(?:betriebs)?spannung|Nennspannung|Betriebsspannung|tension\s+(?:assign[ée]e|nominale|d.emploi|d.alimentation)|tensione\s+(?:nominale|di\s+esercizio))|(?-i:\bUe\b)(?=\s*[:=|(])");
        static readonly Regex LblCoil = L(@"(?:coil\s+voltage|control\s+(?:circuit\s+)?(?:supply\s+)?voltage|actuating\s+voltage|Steuerspannung|Spulenspannung|tension\s+(?:de\s+)?(?:commande|bobine)|tensione\s+(?:di\s+)?(?:comando|bobina))|(?-i:\b(?:Us|Uc)\b)(?=\s*[:=|(])");
        static readonly Regex LblCurrent = L(@"(?:rated\s+(?:operational\s+|operating\s+|thermal\s+)?current|operating\s+current|current\s+rating|nominal\s+current|Bemessungs(?:betriebs)?strom|Nennstrom|courant\s+(?:assign[ée]|nominal|d.emploi)|corrente\s+nominale)|(?-i:\b(?:Ie|In)\b)(?=\s*[:=|(])");
        static readonly Regex LblPower = L(@"(?:rated\s+(?:output\s+|motor\s+)?power|power\s+rating|output\s+power|motor\s+power|Nennleistung|Bemessungsleistung|Motorleistung|puissance\s+(?:nominale|assign[ée]e)|potenza\s+nominale)");
        static readonly Regex LblFreq = L(@"(?:(?:rated\s+|operating\s+|supply\s+)?frequency|Frequenz|fr[ée]quence|frequenza)");
        static readonly Regex LblPoles = L(@"(?:number\s+of\s+poles|\bpoles\b|Polzahl|Polanzahl|nombre\s+de\s+p[ôo]les|numero\s+(?:di\s+)?poli)");
        static readonly Regex LblTemp = L(@"(?:temperature\s+range(?:\s*[-–]\s*operating)?|(?:operating|ambient|working)\s+temperature|Umgebungstemperatur|Betriebstemperatur|temp[ée]rature\s+(?:ambiante|de\s+fonctionnement))");
        static readonly Regex LblWidth = L(@"(?:\bwidth\b|\bBreite\b|\blargeur\b|\blarghezza\b)");
        static readonly Regex LblHeight = L(@"(?:\bheight\b|\bH[öo]he\b|\bhauteur\b|\baltezza\b)");
        static readonly Regex LblDepth = L(@"(?:\bdepth\b|\bTiefe\b|\bprofondeur\b|\bprofondit[àa]\b)");
        static readonly Regex LblWeight = L(@"(?:net\s+weight|\bweight\b|\bGewicht\b|\bpoids\b|\bpeso\b|\bmass\b)");
        static readonly Regex LblSeries = L(@"(?:\bseries(?:\s+(?:number|no\.?|nr\.?))?|\bserie|\bBaureihe|\bgamme)\s*[:|]");
        static readonly Regex SeriesTitleRx = new Regex(@"\b(?<s>[A-Z][A-Za-z]+\s+\d{2,4})-series\b", Cs);
        static readonly Regex LblFamily = L(@"(?:product\s+(?:range|family)|\bfamily\b)\s*[:|]?");
        static readonly Regex LblCategory = L(@"(?:product\s+)?\bcategory\b\s*[:|]?|\bKategorie\b|\bcat[ée]gorie\b");
        static readonly Regex LblDescription = L(@"(?:product\s+)?\bdescription\b\s*[:|]?|\bBeschreibung\b|\bd[ée]signation\b|\bdescrizione\b");
        static readonly Regex LblCircuits = L(@"\bcircuits?(?:\s*\(max(?:imum)?\))?|number\s+of\s+(?:positions|contacts|pins)|\bpositions\b");
        static readonly Regex LblEan = L(@"\b(?:EAN|GTIN|UPC)(?:[- ]?(?:12|13|14))?\b");
        static readonly Regex LblPartNo = L(@"(?:order(?:ing)?\s*(?:no\.?|number|code|ref(?:erence)?)|article\s*(?:no\.?|number|code|nr\.?)|part\s*(?:no\.?|number|nr\.?)|catalog(?:ue)?\s*(?:no\.?|number)|cat\.?\s*no\.?|item\s*(?:no\.?|number)|\bMLFB\b|type\s*(?:no\.?|code|designation)|r[ée]f[ée]rence|Bestell(?:nummer|[- ]?nr\.?)|Artikel(?:nummer|[- ]?nr\.?)|\bTyp\b|\bType\b)");

        static readonly Regex CoilWords = L(@"coil|control|steuer|spule|commande|bobine|comando|bobina");
        static readonly Regex IpRx = new Regex(@"\bIP\s?(?<v>[0-9]{2}[A-Z]?)\b", Cs);
        static readonly Regex PoleRx = new Regex(@"\b(?<v>[1-4])\s?-?\s?pole(?:s)?\b|\b(?<v>[1-4])P\b", Ci);
        static readonly Regex ContactsA = new Regex(@"\b(?<a>\d)\s?NO\b(?:\s*[+&,/]\s*|\s+and\s+)(?<b>\d)\s?NC\b", Ci);
        static readonly Regex ContactsB = new Regex(@"\b(?<b>\d)\s?NC\b(?:\s*[+&,/]\s*|\s+and\s+)(?<a>\d)\s?NO\b", Ci);
        static readonly Regex ContactNo = new Regex(@"\b(?<a>\d)\s?NO\b", Cs);
        static readonly Regex ContactNc = new Regex(@"\b(?<b>\d)\s?NC\b", Cs);
        static readonly Regex MountRx = new Regex(@"(?:35\s?mm\s+)?DIN[\s-]?rail(?:\s+(?:mount(?:ing)?|TS\s?35))?|panel[\s-]mount(?:ing)?(?!\s*[|:]?\s*(?:No|N/A)\b)|flush[\s-]mount(?:ing)?(?!\s*[|:]?\s*(?:No|N/A)\b)|screw\s+(?:mount|fixing)|Hutschiene|Tragschiene|rail\s+DIN", Ci);
        static readonly Regex EanDigits = new Regex(@"\b\d{12,14}\b", Cs);
        static readonly Regex HeaderWords = L(@"\b(?:order|article|type|catalog|catalogue|part|MLFB|Bestell|Artikel|r[ée]f)");

        // ───────── helpers ─────────

        static List<string> Collect(List<TextLine> lines, Regex label, Func<string, string> value, Func<string, bool> accept)
        {
            var found = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Text;
                if (accept != null && !accept(line)) continue;
                var m = label.Match(line);
                if (!m.Success) continue;
                string rest = line.Substring(m.Index + m.Length);
                string v = value(rest);
                if (v == null && i + 1 < lines.Count && Regex.Replace(rest, @"[\s:|.=]", "").Length == 0)
                    v = value(lines[i + 1].Text);
                if (v != null && !found.Contains(v)) found.Add(v);
            }
            return found;
        }

        static void Put(PartRecord p, string key, List<string> found)
        {
            if (found.Count == 0) return;
            p.Set(key, found[0]);
            if (found.Count > 1)
            {
                string reason = "Also found: " + string.Join("; ", found.Skip(1).Take(3));
                p.Flags[key] = reason.Length > 110 ? reason.Substring(0, 110) : reason;
            }
        }

        static void Guess(PartRecord p, string key, string value, string reason)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            p.Set(key, value);
            p.Flags[key] = reason;
        }

        static string FirstGroup(IEnumerable<string> texts, Regex rx, string group)
        {
            foreach (var t in texts)
            {
                var m = rx.Match(t);
                if (m.Success) return m.Groups[group].Value;
            }
            return null;
        }

        // ───────── part numbers ─────────

        static readonly Regex PnToken = new Regex(@"^(?<pn>[A-Za-z0-9][A-Za-z0-9\-\./_]{3,28})(?![A-Za-z0-9\-\./_])", Cs);
        static readonly Regex UnitLike = new Regex(@"^\d+(?:[.,]\d+)?[A-Za-z]{1,3}$", Cs);

        internal static bool IsPartNumber(string s, bool labeled)
        {
            if (s.Length < 4 || s.Length > 30) return false;
            bool digit = s.Any(char.IsDigit), letter = s.Any(char.IsLetter);
            if (!digit) return false;
            if (UnitLike.IsMatch(s)) return false;
            if (Regex.IsMatch(s, @"^(?:19|20)\d\d$")) return false;
            if (Regex.IsMatch(s, @"^IP\d\d", Cs)) return false;
            if (!letter) return labeled && s.Length >= 5 && s.Length <= 12;
            return true;
        }

        static string TrimPn(string s)
        {
            return s.TrimEnd('.', '-', '/', '_');
        }

        static string PartNumberValue(string rest)
        {
            string t = rest.TrimStart(' ', ':', '|', '.', '-', '\t');
            var m = PnToken.Match(t);
            if (!m.Success) return null;
            string pn = TrimPn(m.Groups["pn"].Value);
            return IsPartNumber(pn, true) ? pn : null;
        }

        static List<string> TableVariants(List<TextLine> lines)
        {
            var found = new List<string>();
            for (int i = 0; i < lines.Count && found.Count < 24; i++)
            {
                var cells = lines[i].Text.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length < 2) continue;
                string first = cells[0].Trim();
                var m = PnToken.Match(first);
                if (!m.Success || m.Groups["pn"].Value.Length != first.Length) continue;
                string pn = TrimPn(first);
                if (!IsPartNumber(pn, false) || pn.Length < 6) continue;

                bool headerNear = false;
                for (int k = Math.Max(0, i - 12); k < i && !headerNear; k++)
                    headerNear = HeaderWords.IsMatch(lines[k].Text);
                if (headerNear && !found.Contains(pn)) found.Add(pn);
            }
            return found;
        }

        static string GuessFromHeader(List<TextLine> lines)
        {
            var rx = new Regex(@"\b[A-Z0-9][A-Z0-9\-\./]{5,24}\b", Cs);
            var counts = new Dictionary<string, int>();
            foreach (var l in lines.Take(40))
                foreach (Match m in rx.Matches(l.Text))
                {
                    string pn = TrimPn(m.Value);
                    if (pn.Any(char.IsLetter) && IsPartNumber(pn, false) && !counts.ContainsKey(pn)) counts[pn] = 0;
                }
            if (counts.Count == 0) return null;
            string all = string.Join("\n", lines.Select(l => l.Text));
            foreach (var k in counts.Keys.ToList()) counts[k] = Regex.Matches(all, Regex.Escape(k)).Count;
            var best = counts.OrderByDescending(kv => kv.Value).First();
            return best.Value >= 2 ? best.Key : null;
        }

        // ───────── manufacturer and category ─────────

        static readonly string[][] Makers =
        {
            new[] { "Siemens", "Siemens" }, new[] { "Schneider Electric", "Schneider Electric" }, new[] { "Telemecanique", "Schneider Electric" },
            new[] { "(?-i:ABB)", "ABB" }, new[] { "Eaton", "Eaton" }, new[] { "Moeller", "Eaton" },
            new[] { "Phoenix Contact", "Phoenix Contact" }, new[] { "(?-i:WAGO)", "WAGO" }, new[] { "Rockwell Automation", "Rockwell Automation" },
            new[] { "Allen[- ]Bradley", "Allen-Bradley" }, new[] { "Omron", "Omron" }, new[] { "Mitsubishi Electric", "Mitsubishi Electric" },
            new[] { "Weidm[üu]ller", "Weidmüller" }, new[] { "Hager", "Hager" }, new[] { "Legrand", "Legrand" }, new[] { "Rittal", "Rittal" },
            new[] { "Murrelektronik", "Murrelektronik" }, new[] { "Pilz", "Pilz" }, new[] { "(?-i:LAPP)", "Lapp" }, new[] { "Beckhoff", "Beckhoff" },
            new[] { "(?-i:ifm)", "ifm" }, new[] { "(?-i:SICK)", "SICK" }, new[] { "Festo", "Festo" }, new[] { "Danfoss", "Danfoss" },
            new[] { "Lenze", "Lenze" }, new[] { "SEW[- ]Eurodrive", "SEW-Eurodrive" }, new[] { "Turck", "Turck" }, new[] { "Pepperl\\+Fuchs", "Pepperl+Fuchs" },
            new[] { "(?-i:Finder)", "Finder" }, new[] { "TE Connectivity", "TE Connectivity" }, new[] { "(?-i:HARTING)", "HARTING" },
            new[] { "Lovato", "Lovato Electric" }, new[] { "Carlo Gavazzi", "Carlo Gavazzi" }, new[] { "(?-i:IDEC)", "IDEC" }, new[] { "Fuji Electric", "Fuji Electric" },
            new[] { "Delta Electronics", "Delta Electronics" }, new[] { "Mean Well", "Mean Well" }, new[] { "(?-i:PULS)", "PULS" }, new[] { "Crouzet", "Crouzet" },
            new[] { "Altech", "Altech" }, new[] { "Entrelec", "ABB" }, new[] { "(?-i:CHINT)", "CHINT" }, new[] { "Socomec", "Socomec" },
            new[] { "Banner Engineering", "Banner Engineering" }, new[] { "Leuze", "Leuze" }, new[] { "Balluff", "Balluff" }, new[] { "Keyence", "Keyence" },
            new[] { "Panasonic", "Panasonic" }, new[] { "Yaskawa", "Yaskawa" }, new[] { "Schmersal", "Schmersal" }, new[] { "Euchner", "Euchner" },
            new[] { "Automation\\s?Direct", "AutomationDirect" }, new[] { "Molex", "Molex" }, new[] { "Amphenol", "Amphenol" }, new[] { "(?-i:JST)", "JST" }, new[] { "Hirose", "Hirose" }, new[] { "Samtec", "Samtec" },
            new[] { "Harwin", "Harwin" }, new[] { "W[üu]rth Elektronik", "Würth Elektronik" }, new[] { "Lumberg", "Lumberg" }, new[] { "(?-i:binder)", "binder" },
            new[] { "Neutrik", "Neutrik" }, new[] { "Switchcraft", "Switchcraft" }, new[] { "ITT Cannon", "ITT Cannon" }, new[] { "Glenair", "Glenair" },
            new[] { "Bulgin", "Bulgin" }, new[] { "Hubbell", "Hubbell" }, new[] { "Bel Fuse", "Bel Fuse" }, new[] { "Littelfuse", "Littelfuse" },
            new[] { "Bourns", "Bourns" }, new[] { "Vishay", "Vishay" }, new[] { "Panduit", "Panduit" }, new[] { "Bosch Rexroth", "Bosch Rexroth" },
            new[] { "Autonics", "Autonics" }, new[] { "Bticino", "Bticino" }, new[] { "Sprecher\\s?\\+\\s?Schuh", "Sprecher + Schuh" }
        };

        static readonly Regex Copyright = new Regex(@"(?:©|\(c\)|copyright)\s*(?:\d{4}\s*)?(?<m>[A-Z][A-Za-z0-9&.\- ]{2,40}?)(?:\s+(?:Ltd|GmbH|AG|Inc|SA|S\.A\.|SE|Co\b)|[.,;|]|$)", Ci);

        static void FindManufacturer(PartRecord p, PdfContent c)
        {
            var lines = c.Lines;
            var head = string.Join("\n", lines.Take(15).Select(l => l.Text)) + "\n" + c.Author + "\n" + c.Title;
            string all = string.Join("\n", lines.Select(l => l.Text));
            string best = null;
            int bestScore = 0;
            foreach (var mk in Makers)
            {
                var rx = new Regex(@"(?<![A-Za-z])(?:" + mk[0] + @")(?![A-Za-z])", Ci);
                int score = rx.Matches(all).Count + rx.Matches(head).Count * 5;
                if (score > bestScore) { bestScore = score; best = mk[1]; }
            }
            if (best != null) { p.Set("manufacturer", best); return; }

            foreach (var l in lines)
            {
                var m = Copyright.Match(l.Text);
                if (m.Success)
                {
                    Guess(p, "manufacturer", m.Groups["m"].Value.Trim(), "Taken from a copyright line");
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(c.Author)) Guess(p, "manufacturer", c.Author.Trim(), "Taken from the PDF author field");
        }

        static KeyValuePair<string, Regex> Cat(string name, string pattern)
        {
            return new KeyValuePair<string, Regex>(name, new Regex(pattern, Ci));
        }

        static readonly KeyValuePair<string, Regex>[] Categories =
        {
            Cat("Contactor", @"contactor|sch[üu]tz|contacteur|contattore"),
            Cat("Circuit breaker", @"circuit[\s-]?breaker|\bMCB\b|\bMCCB\b|\bACB\b|leistungsschalter|disjoncteur|interruttore\s+automatico"),
            Cat("Motor protection breaker", @"motor\s+protection|motorschutz|disjoncteur[\s-]moteur"),
            Cat("Overload relay", @"overload\s+relay|thermal\s+overload|motorschutzrelais|[üu]berlastrelais"),
            Cat("Relay", @"\brelay\b|\brelais\b|\brel[èe]\b"),
            Cat("Push button", @"push[\s-]?button|drucktaster|bouton[\s-]poussoir|pulsante"),
            Cat("Selector switch", @"selector\s+switch|wahlschalter|commutateur|selettore"),
            Cat("Indicator light", @"indicator\s+light|pilot\s+light|signal\s+lamp|leuchtmelder|voyant"),
            Cat("Emergency stop", @"emergency[\s-]?stop|not-?halt|arr[êe]t\s+d.urgence"),
            Cat("Terminal block", @"terminal\s+block|reihenklemme|feed[\s-]through\s+terminal|\bborne\b|morsetto"),
            Cat("Fuse or fuse holder", @"\bfuse|sicherung|fusible|fusibile"),
            Cat("Power supply", @"power\s+supply|netzteil|alimentation|alimentatore"),
            Cat("PLC or I/O module", @"\bPLC\b|\bI/O\b|digital\s+(?:input|output)|analog(?:ue)?\s+(?:input|output)"),
            Cat("Drive or inverter", @"frequency\s+(?:converter|inverter)|\binverter\b|variable\s+(?:frequency|speed)\s+drive|\bVFD\b|frequenzumrichter|variateur|\bAC\s+drives?\b|adjustable\s+frequency"),
            Cat("Sensor", @"sensor|proximity|photoelectric|n[äa]herungsschalter|capteur"),
            Cat("Connector", @"connector|stecker|connecteur"),
            Cat("Transformer", @"transformer|transformator|transformateur"),
            Cat("Enclosure", @"enclosure|geh[äa]use|coffret"),
            Cat("Cable", @"\bcable\b|\bkabel\b|c[âa]ble")
        };

        static void FindCategory(PartRecord p, List<TextLine> lines)
        {
            string top = string.Join("\n", lines.Take(8).Select(l => l.Text));
            string head = string.Join("\n", lines.Take(30).Select(l => l.Text));
            string all = string.Join("\n", lines.Select(l => l.Text));
            string best = null;
            int bestScore = 0;
            foreach (var kv in Categories)
            {
                int score = kv.Value.Matches(top).Count * 10 + kv.Value.Matches(head).Count * 3 + kv.Value.Matches(all).Count;
                if (score > bestScore) { bestScore = score; best = kv.Key; }
            }
            if (best != null && bestScore >= 3) Guess(p, "category", best, "Guessed from keywords");
        }

        // ───────── ratings tables: one part per row (drive and motor-control catalogs) ─────────

        sealed class RatingRow
        {
            public string PartNumber, Model, Group, NdHp, NdKw, HdHp, HdKw, OutA, InRange, Watts, Frame;
        }

        static readonly Regex GroupRx = new Regex(@"^\d{2,3}\s*(?:\.\.\.|…|–|—|-)\s*\d{2,3}\s*V\s*(?:AC|DC)\b.*\bInput\b", Ci);
        static readonly Regex NumTok = new Regex(@"^\d+(?:\.\d+)?$", Cs);
        static readonly Regex RangeTok = new Regex(@"^\d+(?:\.\d+)?(?:\.\.\.|…|–|—|-)\d+(?:\.\d+)?$", Cs);
        static readonly Regex ModelRx = new Regex(@"\b[A-Z][A-Za-z]+\s+\d{3,4}\b", Cs);
        static readonly Regex FrameRowRx = new Regex(@"^(?<f>[A-Z])[\s|]+(?<rest>(?:\d+(?:\.\d+)?\s*\(\d+(?:\.\d+)?\)[\s|]*){6})$", Cs);

        static string[] Tokens(string line)
        {
            return Regex.Split(line.Trim(), @"\s*\|\s*|\s+").Where(s => s.Length > 0).ToArray();
        }

        static bool IsDash(string s) { return s == "–" || s == "-" || s == "—"; }

        /// <summary>Row layout: catalog number(s), then ND HP, ND kW, HD HP, HD kW, output A, input range, watts loss, frame.</summary>
        static List<RatingRow> ParseRatingsRow(string line, string[] models, string group)
        {
            var tok = Tokens(line);
            var pns = new List<string>();     // null marks a dash (no part in that column)
            int i = 0;
            while (i < tok.Length && i < 3)
            {
                if (IsDash(tok[i])) { pns.Add(null); i++; }
                else if (tok[i].Length >= 6 && tok[i].IndexOf('-') > 0 && IsPartNumber(tok[i], false)) { pns.Add(tok[i]); i++; }
                else break;
            }
            if (!pns.Any(p => p != null)) return null;
            if (tok.Length - i != 8) return null;
            for (int k = 0; k < 5; k++) if (!NumTok.IsMatch(tok[i + k])) return null;
            if (!RangeTok.IsMatch(tok[i + 5]) || !NumTok.IsMatch(tok[i + 6]) || !Regex.IsMatch(tok[i + 7], "^[A-Z]$")) return null;

            var rows = new List<RatingRow>();
            for (int c = 0; c < pns.Count; c++)
            {
                if (pns[c] == null) continue;
                rows.Add(new RatingRow
                {
                    PartNumber = pns[c],
                    Model = models != null && c < models.Length ? models[c] : null,
                    Group = group,
                    NdHp = tok[i], NdKw = tok[i + 1], HdHp = tok[i + 2], HdKw = tok[i + 3],
                    OutA = tok[i + 4], InRange = tok[i + 5], Watts = tok[i + 6], Frame = tok[i + 7]
                });
            }
            return rows;
        }

        static List<RatingRow> RatingsRows(List<TextLine> lines)
        {
            var rows = new List<RatingRow>();
            string[] models = null;
            string group = null;
            bool inTable = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Text;
                if (Regex.IsMatch(t, @"Catalog\s+Number", Ci))
                {
                    bool ratings = false;
                    for (int k = Math.Max(0, i - 4); k < i; k++)
                        if (Regex.IsMatch(lines[k].Text, @"Normal\s+Duty", Ci) && Regex.IsMatch(lines[k].Text, @"Frame\s+Size", Ci)) ratings = true;
                    inTable = ratings;
                    if (ratings)
                    {
                        // the nearest line above that names two or more models ("PowerFlex 523 | PowerFlex 525")
                        models = null;
                        for (int k = i - 1; k >= Math.Max(0, i - 6) && models == null; k--)
                        {
                            var ms = ModelRx.Matches(lines[k].Text).Cast<Match>().Select(m => m.Value).ToArray();
                            if (ms.Length >= 2) models = ms;
                        }
                    }
                    continue;
                }
                if (!inTable) continue;
                if (GroupRx.IsMatch(t)) { group = t; continue; }
                var parsed = ParseRatingsRow(t, models, group);
                if (parsed != null) rows.AddRange(parsed);
            }
            return rows.GroupBy(r => r.PartNumber).Select(g => g.First()).Take(MaxVariants).ToList();
        }

        /// <summary>Frame size to width, height, depth and weight, from a "Dimensions and Weight" table.</summary>
        static Dictionary<string, double[]> FrameTable(List<TextLine> lines)
        {
            var map = new Dictionary<string, double[]>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (!Regex.IsMatch(lines[i].Text, @"Dimensions\s+and\s+Weight", Ci)) continue;
                for (int k = i + 1; k < lines.Count && k <= i + 40; k++)
                {
                    var m = FrameRowRx.Match(lines[k].Text);
                    if (!m.Success) continue;
                    var nums = Regex.Matches(m.Groups["rest"].Value, @"(\d+(?:\.\d+)?)\s*\(").Cast<Match>().Select(x => ParseNumber(x.Groups[1].Value)).ToArray();
                    map[m.Groups["f"].Value] = nums;
                }
                if (map.Count > 0) break;
            }
            return map;
        }

        static List<PartRecord> BuildFromRatings(PartRecord b, List<RatingRow> rows, List<TextLine> lines, string path)
        {
            var frames = FrameTable(lines);
            const string dimWhy = "Assumed A=width, B=height, C=depth";
            var parts = new List<PartRecord>();
            foreach (var r in rows)
            {
                var p = new PartRecord { SourceFile = path };
                foreach (var kv in b.Values) p.Set(kv.Key, kv.Value);
                foreach (var kv in b.Flags) p.Flags[kv.Key] = kv.Value;
                foreach (var key in new[] { "ratedVoltage", "ratedCurrent", "ratedPower", "width", "height", "depth", "weight", "partNumber", "description" })
                    p.Flags.Remove(key);

                p.Set("partNumber", r.PartNumber);

                string volt = r.Group == null ? null : Voltage(r.Group);
                if (volt != null) p.Set("ratedVoltage", volt);
                p.Set("ratedCurrent", r.OutA);
                double kw = ParseNumber(r.NdKw);
                if (!double.IsNaN(kw)) p.Set("ratedPower", Fmt(kw * 1000));

                string phases = null;
                if (r.Group != null)
                {
                    var pm = Regex.Match(r.Group, @"(?<n>\d)-phase\s+Input", Ci);
                    if (pm.Success) phases = pm.Groups["n"].Value + "-phase";
                }
                bool emc = r.Group != null && Regex.IsMatch(r.Group, @"EMC\s+Filter", Ci);

                string noun = b.Get("category") == "Drive or inverter" ? "AC drive" : "";
                string model = r.Model ?? "";
                string desc = (model + " " + noun).Trim();
                desc += (desc.Length > 0 ? ", " : "") + r.NdKw + " kW (" + r.NdHp + " HP) normal duty, " + r.OutA + " A";
                if (volt != null) desc += ", " + volt + " V" + (phases != null ? " " + phases : "") + " input";
                desc += ", frame " + r.Frame;
                p.Set("description", desc);
                p.Flags["description"] = "Built from the table row";

                double[] fr;
                if (frames.TryGetValue(r.Frame, out fr) && fr.Length >= 6)
                {
                    p.Set("width", Fmt(fr[0]));
                    p.Set("height", Fmt(fr[1]));
                    p.Set("depth", Fmt(fr[2]));
                    p.Set("weight", Fmt(fr[5]));
                    p.Flags["width"] = dimWhy; p.Flags["height"] = dimWhy; p.Flags["depth"] = dimWhy;
                }

                var spec = new List<string>();
                if (r.Model != null) spec.Add("Model: " + r.Model);
                spec.Add("Frame size: " + r.Frame);
                if (phases != null) spec.Add("Input: " + phases);
                spec.Add("EMC filter: " + (emc ? "Yes" : "No"));
                spec.Add("Normal duty: " + r.NdHp + " HP / " + r.NdKw + " kW");
                spec.Add("Heavy duty: " + r.HdHp + " HP / " + r.HdKw + " kW");
                spec.Add("Input voltage range: " + NormRange(r.InRange) + " V");
                spec.Add("Total watts loss: " + r.Watts + " W");
                string existing = p.Get("otherSpecs");
                p.Set("otherSpecs", string.Join("; ", spec) + (existing.Length > 0 ? "; " + existing : ""));

                parts.Add(p);
            }
            foreach (var p in parts)
                foreach (var k in p.Flags.Keys.ToList())
                    if (k != "terminals" && p.Get(k).Length == 0) p.Flags.Remove(k);
            return parts;
        }

        // ───────── price lists and selection guides ─────────

        static string PairVoltages(List<string> volts, List<string> kas)
        {
            var items = new List<string>();
            for (int i = 0; i < volts.Count; i++)
            {
                var m = Regex.Match(volts[i], @"^(?<n>[\dY/]+)\s*(?<p>\(.*\))?$");
                string v = m.Success
                    ? m.Groups["n"].Value + " V" + (m.Groups["p"].Value.Length > 0 ? " " + m.Groups["p"].Value : "")
                    : volts[i];
                items.Add(v + (i < kas.Count ? " = " + kas[i] + " kA" : ""));
            }
            return string.Join("; ", items);
        }

        static List<PartRecord> BuildFromGrid(PartRecord b, List<GridTable> tables, List<SpecColumns> specs, string path)
        {
            var parts = new List<PartRecord>();
            var seen = new HashSet<string>();
            foreach (var t in tables)
            {
                string series = GridTables.CleanSeries(t.Title);
                bool accessory = Regex.IsMatch(t.Title, @"Accessor|Handles|Shafts|Cables|Lugs|Battery", Ci);
                var pm = Regex.Match(t.Title, @"\b(?<n>\d)-Pole\b(?!\s+or\b)", Ci);
                string poles = !accessory && pm.Success ? pm.Groups["n"].Value : null;

                Func<string, int[]> colsLike = rx => Enumerable.Range(0, t.Labels.Count).Where(j => Regex.IsMatch(t.Labels[j], rx, Ci)).ToArray();
                var acv = colsLike(@"Voltage\s*\(AC\)").Where(j => t.Groups.ContainsKey(j)).ToArray();
                var dcv = colsLike(@"Voltage\s*\(DC\)").Where(j => t.Groups.ContainsKey(j)).ToArray();
                var kas = colsLike("Interrupt").Where(j => t.Groups.ContainsKey(j)).ToArray();

                string freq = null;
                foreach (int j in colsLike("Frequency"))
                {
                    if (t.Groups.ContainsKey(j) && t.Groups[j].Count > 0)
                    {
                        freq = Regex.Replace(t.Groups[j][0], @"\s*Hz\b", "", Ci).Trim();
                        break;
                    }
                }

                var tableSpecs = new List<string>();
                string ratedVoltage = null;
                if (acv.Length > 0)
                {
                    int ka = kas.FirstOrDefault(j => j > acv[0]);
                    var kaList = ka > acv[0] && t.Groups.ContainsKey(ka) ? t.Groups[ka] : new List<string>();
                    ratedVoltage = string.Join("; ", t.Groups[acv[0]]) + " AC";
                    tableSpecs.Add("AC interrupt capacity: " + PairVoltages(t.Groups[acv[0]], kaList));
                }
                if (dcv.Length > 0)
                {
                    int ka = kas.FirstOrDefault(j => j > dcv[0]);
                    var kaList = ka > dcv[0] && t.Groups.ContainsKey(ka) ? t.Groups[ka] : new List<string>();
                    tableSpecs.Add("DC interrupt capacity: " + PairVoltages(t.Groups[dcv[0]], kaList));
                }

                var spec = poles == null ? null : specs.FirstOrDefault(s => s.Series == series && s.ByPoles.ContainsKey(int.Parse(poles)));

                for (int r = 0; r < t.PartNumbers.Count; r++)
                {
                    string pn = t.PartNumbers[r];
                    if (!seen.Add(pn)) continue;
                    var row = t.Rows[r];
                    Func<string, string> cellOf = rx =>
                    {
                        foreach (var kv in row)
                            if (Regex.IsMatch(t.Labels[kv.Key], rx, Ci) && kv.Value.Length > 0) return kv.Value;
                        return null;
                    };

                    var p = new PartRecord { SourceFile = path };
                    foreach (var kv in b.Values) p.Set(kv.Key, kv.Value);
                    foreach (var kv in b.Flags) p.Flags[kv.Key] = kv.Value;
                    foreach (var key in new[] { "partNumber", "description", "ratedCurrent", "ratedVoltage", "frequency", "poles", "weight", "unitPrice", "currency", "series" })
                        p.Flags.Remove(key);

                    p.Set("partNumber", pn);
                    if (series.Length > 0) p.Set("series", series);

                    var pr = Regex.Match(cellOf("^Price$") ?? "", @"^(?<c>[\$€£])\s?(?<n>[\d,]+(?:\.\d+)?)$");
                    if (pr.Success)
                    {
                        string sym = pr.Groups["c"].Value;
                        p.Set("unitPrice", pr.Groups["n"].Value.Replace(",", ""));
                        p.Set("currency", sym == "$" ? "USD" : sym == "€" ? "EUR" : "GBP");
                        if (sym == "$") p.Flags["currency"] = "Assumed US dollars from the $ sign";
                    }

                    string amp = null;
                    foreach (var kv in row)
                        if (amp == null && Regex.IsMatch(t.Labels[kv.Key], "Ampere|Amps|Rating", Ci) && Regex.IsMatch(kv.Value, @"^\d+(?:\.\d+)?$"))
                            amp = kv.Value;
                    if (amp != null) p.Set("ratedCurrent", amp);
                    if (freq != null) p.Set("frequency", freq);
                    if (poles != null) p.Set("poles", poles);
                    if (ratedVoltage != null)
                    {
                        p.Set("ratedVoltage", ratedVoltage);
                        p.Flags["ratedVoltage"] = "Several voltage ratings listed";
                    }

                    string desc = cellOf("description");
                    if (desc != null) p.Set("description", desc);
                    else
                    {
                        string built = series + (poles != null ? " " + poles + "-pole" : "") + (amp != null ? ", " + amp + " A" : "");
                        p.Set("description", built.Trim());
                        p.Flags["description"] = "Built from the table row";
                    }

                    string baseCat = b.Get("category");
                    p.Set("category", accessory ? (baseCat.Length > 0 ? baseCat + " accessory" : "Accessory") : baseCat);

                    var other = new List<string>(tableSpecs);
                    if (spec != null)
                    {
                        var d = spec.ByPoles[int.Parse(poles)];
                        string w;
                        if (d.TryGetValue("weight", out w)) p.Set("weight", w);
                        string v;
                        if (d.TryGetValue("Ui", out v)) other.Add("Insulation voltage Ui: " + v);
                        if (d.TryGetValue("Uimp", out v)) other.Add("Impulse withstand Uimp: " + v);
                        if (d.TryGetValue("cat", out v)) other.Add("Utilization category: " + v);
                        if (d.TryGetValue("Ics", out v)) other.Add("Service breaking capacity Ics: " + v);
                    }
                    string existing = b.Get("otherSpecs");
                    if (existing.Length > 0) other.Add(existing);
                    p.Set("otherSpecs", string.Join("; ", other));
                    parts.Add(p);
                    if (parts.Count >= MaxVariants) return parts;
                }
            }
            foreach (var p in parts)
                foreach (var k in p.Flags.Keys.ToList())
                    if (k != "terminals" && p.Get(k).Length == 0) p.Flags.Remove(k);
            return parts;
        }

        static string Cell(string s)
        {
            s = s.TrimStart(' ', ':', '|', '\t').Trim();
            int cut = s.IndexOf(" | ", StringComparison.Ordinal);
            return cut > 0 ? s.Substring(0, cut).Trim() : s;
        }

        /// <summary>The text after a "Description" label, joined with the wrapped lines that follow it.</summary>
        static string DescriptionFromLabels(List<TextLine> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var m = LblDescription.Match(lines[i].Text);
                if (!m.Success) continue;
                string first = Cell(lines[i].Text.Substring(m.Index + m.Length));
                if (first.Length < 8 || Regex.IsMatch(first, @"^part\s+number$", Ci)) continue;

                var sb = new System.Text.StringBuilder(first);
                for (int k = i + 1; k < lines.Count && k <= i + 3; k++)
                {
                    string next = lines[k].Text;
                    if (next.Contains(" | ") || next.Contains(" : ") || Regex.IsMatch(next, @"^[-•]\s")) break;
                    if (sb[sb.Length - 1] == '-') sb.Append(next); else sb.Append(' ').Append(next);
                    if (sb.Length > 220) break;
                }
                return sb.ToString().Trim();
            }
            return null;
        }

        static readonly Regex SpecLabel = L(@"^(?:pitch(?:\s*-\s*[A-Za-z ]+)?|colou?r(?:\s*-\s*[A-Za-z ]+)?|material(?:\s*-\s*[A-Za-z ]+)?|flammability|gender|number\s+of\s+rows|polarized(?:\s+to\s+mating\s+part)?|keying(?:\s+to\s+mating\s+part)?|lock\s+to\s+mating\s+part|stackable|panel\s+mount|termination(?:\s+[A-Za-z ]+)?|wire\s+(?:size|gauge)(?:\s*-\s*[A-Za-z ]+)?|plating(?:\s*-\s*[A-Za-z ]+)?|glow-wire\s+capable|application|product\s+family|product\s+name|packaging\s+type)$");

        /// <summary>Notable "Label | Value" rows that have no field of their own (pitch, colour, material, and so on).</summary>
        static string OtherSpecs(List<TextLine> lines)
        {
            var items = new List<string>();
            foreach (var l in lines)
            {
                var cells = l.Text.Split(new[] { " | " }, StringSplitOptions.None);
                if (cells.Length != 2) continue;
                string label = cells[0].Trim(), value = cells[1].Trim();
                if (value.Length == 0 || value.Length > 60 || !SpecLabel.IsMatch(label)) continue;
                string item = label + ": " + value;
                if (!items.Contains(item)) items.Add(item);
                if (items.Count >= 14) break;
            }
            return items.Count == 0 ? null : string.Join("; ", items);
        }

        static string GuessTitle(List<TextLine> lines)
        {
            var skip = new Regex(@"^(?:data\s*sheet|datasheet|technical\s+data|product\s+data(?:\s+sheet)?|datenblatt|fiche\s+technique|documents?\s*&\s*resources|product\s+environment\s+compliance|part\s+details|compliance|general|physical|agency|specifications|drawings)$", Ci);
            var pool = lines.Where(l => l.Page == 1 && l.Text.Length >= 12 && l.Text.Length <= 140 && !skip.IsMatch(l.Text)).ToList();
            if (pool.Count == 0) pool = lines.Where(l => l.Text.Length >= 12 && l.Text.Length <= 140 && !skip.IsMatch(l.Text)).Take(10).ToList();
            if (pool.Count == 0) return null;
            TextLine best = null;
            double bestSize = -1;
            foreach (var l in pool)
                if (l.FontSize > bestSize) { bestSize = l.FontSize; best = l; }
            return best == null ? null : best.Text.Replace(" | ", " ").Trim();
        }

        // ───────── main parse ─────────

        static List<PartRecord> Parse(PdfContent c, string path)
        {
            var lines = c.Lines;
            var b = new PartRecord { SourceFile = path };
            foreach (var f in Schema.Fields) b.Set(f.Key, "");

            FindManufacturer(b, c);

            Put(b, "ratedVoltage", Collect(lines, LblVoltage, Voltage, ln => !CoilWords.IsMatch(ln)));
            Put(b, "coilVoltage", Collect(lines, LblCoil, Voltage, null));
            Put(b, "ratedCurrent", Collect(lines, LblCurrent, Current, null));
            Put(b, "ratedPower", Collect(lines, LblPower, Power, null));
            Put(b, "frequency", Collect(lines, LblFreq, Frequency, null));
            Put(b, "tempRange", Collect(lines, LblTemp, Temperature, null));
            Put(b, "weight", Collect(lines, LblWeight, Kilograms, null));
            Put(b, "width", Collect(lines, LblWidth, Millimetres, null));
            Put(b, "height", Collect(lines, LblHeight, Millimetres, null));
            Put(b, "depth", Collect(lines, LblDepth, Millimetres, null));

            // Poles: labelled value first, then "3-pole" / "3P" wording.
            Func<string, string> leadingNumber = rest => { var m = Regex.Match(rest, @"^[\s:|.=]*(?<v>\d{1,3})(?!\d)"); return m.Success ? m.Groups["v"].Value : null; };
            var poles = Collect(lines, LblPoles, leadingNumber, null);
            if (poles.Count > 0) Put(b, "poles", poles);
            else
            {
                var pm = lines.Take(60).Select(l => PoleRx.Match(l.Text)).FirstOrDefault(m => m.Success);
                if (pm != null) Guess(b, "poles", pm.Groups["v"].Value, "Read from the wording, not a table");
            }
            if (b.Get("poles").Length == 0)
            {
                var circuits = Collect(lines, LblCircuits, leadingNumber, null);
                if (circuits.Count > 0) Guess(b, "poles", circuits[0], "Taken from the circuits or positions count");
            }

            // Dimensions given as one "W x H x D" triple, when no labelled values were found.
            if (b.Get("width").Length == 0 && b.Get("height").Length == 0 && b.Get("depth").Length == 0)
            {
                foreach (var l in lines)
                {
                    var tm = TripleRx.Match(l.Text);
                    if (!tm.Success) continue;
                    double mult = tm.Groups["u"].Value == "cm" ? 10 : 1;
                    double x = ParseNumber(tm.Groups["a"].Value) * mult, y = ParseNumber(tm.Groups["b"].Value) * mult, z = ParseNumber(tm.Groups["c"].Value) * mult;
                    if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) continue;
                    const string why = "Axis order assumed W x H x D";
                    Guess(b, "width", Fmt(x), why);
                    Guess(b, "height", Fmt(y), why);
                    Guess(b, "depth", Fmt(z), why);
                    break;
                }
            }

            // IP rating
            // Rank candidates: wording like "standard IP 20" or "IP 20/Open Type" marks the product itself,
            // "optional IP 30" marks an add-on kit. Otherwise the most frequently quoted rating wins.
            var ipScore = new Dictionary<string, int>();
            var ips = new List<string>();
            foreach (var l in lines)
                foreach (Match m in IpRx.Matches(l.Text))
                {
                    string v = "IP" + m.Groups["v"].Value;
                    if (!ipScore.ContainsKey(v)) { ipScore[v] = 0; ips.Add(v); }
                    string before = l.Text.Substring(Math.Max(0, m.Index - 14), Math.Min(14, m.Index)).ToLowerInvariant();
                    int endAt = m.Index + m.Length;
                    string after = l.Text.Substring(endAt, Math.Min(14, l.Text.Length - endAt)).ToLowerInvariant();
                    int score = 1;
                    if (before.Contains("standard")) score += 20;
                    if (before.Contains("optional")) score -= 10;
                    if (after.Contains("open")) score += 2;
                    ipScore[v] += score;
                }
            ips = ips.OrderByDescending(v => ipScore[v]).ToList();
            Put(b, "ipRating", ips);

            // Contact configuration
            foreach (var l in lines)
            {
                var ma = ContactsA.Match(l.Text);
                if (ma.Success) { b.Set("contacts", ma.Groups["a"].Value + "NO+" + ma.Groups["b"].Value + "NC"); break; }
                var mb = ContactsB.Match(l.Text);
                if (mb.Success) { b.Set("contacts", mb.Groups["a"].Value + "NO+" + mb.Groups["b"].Value + "NC"); break; }
            }
            if (b.Get("contacts").Length == 0)
            {
                string no = FirstGroup(lines.Take(80).Select(l => l.Text), ContactNo, "a");
                string nc = FirstGroup(lines.Take(80).Select(l => l.Text), ContactNc, "b");
                if (no != null || nc != null)
                    Guess(b, "contacts", (no != null ? no + "NO" : "") + (no != null && nc != null ? "+" : "") + (nc != null ? nc + "NC" : ""), "Read from loose wording");
            }

            // Mounting
            string mount = FirstGroup(lines.Select(l => l.Text), new Regex("(?<v>" + MountRx + ")", Ci), "v");
            if (mount != null) b.Set("mounting", Regex.Replace(mount, @"\s+", " ").Trim());

            // Series, EAN
            Func<string, string> cellValue = rest =>
            {
                string t = Cell(rest);
                return t.Length >= 2 && t.Length <= 60 && t.IndexOf(':') < 0 ? t : null;
            };
            var series = Collect(lines, LblSeries, cellValue, null);
            if (series.Count == 0)
            {
                var sm = lines.Select(l => SeriesTitleRx.Match(l.Text)).FirstOrDefault(m => m.Success);
                if (sm != null) series.Add(sm.Groups["s"].Value + "-series");
            }
            if (series.Count == 0) series = Collect(lines, LblFamily, cellValue, null);
            Put(b, "series", series);

            var eans = Collect(lines, LblEan, rest => { var m = EanDigits.Match(rest); return m.Success ? m.Value : null; }, null);
            Put(b, "ean", eans);

            // Category and description: use the sheet's own wording first, guess only as a last resort
            var cats = Collect(lines, LblCategory, rest => { string t = Cell(rest); return t.Length >= 3 && t.Length <= 60 && t.IndexOf(':') < 0 ? t : null; }, null);
            if (cats.Count > 0) Put(b, "category", cats);
            else FindCategory(b, lines);

            string desc = DescriptionFromLabels(lines);
            if (desc != null) b.Set("description", desc);
            else Guess(b, "description", GuessTitle(lines), "Taken from the page title");

            string other = OtherSpecs(lines);
            if (other != null) b.Set("otherSpecs", other);

            // Catalog sheets list one orderable part per table row: read the rows when there are several
            var ratingRows = RatingsRows(lines);
            if (ratingRows.Count >= 2) return BuildFromRatings(b, ratingRows, lines, path);

            // Price lists and selection guides: one part per row, with merged cells and a variant-per-column spec table
            var gridTables = GridTables.Read(lines);
            if (gridTables.Count > 0)
            {
                var gridParts = BuildFromGrid(b, gridTables, GridTables.ReadSpecs(lines), path);
                if (gridParts.Count >= 2) return gridParts;
            }

            // Part numbers: table rows (one part per row) or the labelled order number
            var variants = new List<string>();
            var table = TableVariants(lines);
            var labelled = Collect(lines, LblPartNo, PartNumberValue, null);
            string pnFlag = null;
            if (table.Count >= 2)
            {
                variants = table;
                pnFlag = "Variant-specific values were not read; check each one";
            }
            else if (labelled.Count > 0)
            {
                variants.Add(labelled[0]);
                if (labelled.Count > 1)
                {
                    pnFlag = "Also found: " + string.Join("; ", labelled.Skip(1).Take(3));
                    if (pnFlag.Length > 110) pnFlag = pnFlag.Substring(0, 110);
                }
            }
            else
            {
                string g = GuessFromHeader(lines);
                if (g != null) { variants.Add(g); pnFlag = "Guessed from the page header"; }
                else
                {
                    variants.Add(Path.GetFileNameWithoutExtension(path));
                    pnFlag = "Taken from the file name";
                }
            }

            var parts = new List<PartRecord>();
            foreach (var pn in variants.Take(MaxVariants))
            {
                var p = new PartRecord { SourceFile = path };
                foreach (var kv in b.Values) p.Set(kv.Key, kv.Value);
                foreach (var kv in b.Flags) p.Flags[kv.Key] = kv.Value;
                p.Set("partNumber", pn);
                if (pnFlag != null) p.Flags["partNumber"] = pnFlag;
                p.Set("description", p.Get("description"));
                parts.Add(p);
            }
            foreach (var p in parts)
                foreach (var k in p.Flags.Keys.ToList())
                    if (k != "terminals" && p.Get(k).Length == 0) p.Flags.Remove(k);
            return parts;
        }
    }
}
