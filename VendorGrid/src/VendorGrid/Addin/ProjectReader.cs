using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VendorGrid.Core;

namespace VendorGrid.Addin
{
    public sealed class ProjectRef
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
        public string Display { get { return IsActive ? Name + "  (active)" : Name; } }
    }

    public sealed class ProjectPart
    {
        public string Manufacturer { get; set; } = "";
        public string Reference { get; set; } = "";       // manufacturer part number
        public string Description { get; set; } = "";
        public string Supplier { get; set; } = "";
        public string StockNumber { get; set; } = "";
        public int Quantity { get; set; } = -1;           // components using this part; -1 = not counted
    }

    public sealed class ProjectSnapshot
    {
        public ProjectRef Project { get; set; }
        public List<ProjectPart> Parts { get; } = new List<ProjectPart>();
        public bool QuantitiesComplete { get; set; }
        public string Warning { get; set; }
    }

    /// <summary>
    /// Reads manufacturer parts from the SOLIDWORKS Electrical project that is open and active (or another open project).
    /// Must be called on the host's UI thread. Everything is late bound and defensive: a failure returns "no project",
    /// never an exception, so the manual search bar keeps working.
    ///
    ///   IEwApplicationX.getEwProjectCurrent  →  IEwProjectX.getEwProjectManufacturerPartManager
    ///   →  getEwProjectManufacturerPartArray →  IEwProjectManufacturerPartX.{getReference, getManufacturer, getDescription, ...}
    ///   Quantity: IEwProjectX.getEwProjectComponentManager → getEwProjectComponentArray → IEwProjectComponentX.getEwProjectManufacturerPartArray
    /// </summary>
    internal static class ProjectReader
    {
        const int CountBudgetMs = 4000;
        static readonly string[] FallbackLanguages = { "en-US", "en", "" };
        static string _workingLanguage;   // remembered once a code has returned text

        /// <summary>The IEwApplicationX object received from the host (set by the add-in on connect).</summary>
        public static object App { get; set; }
        public static bool IsConnected { get { return App != null; } }

        /// <summary>
        /// Language code of the active project: SOLIDWORKS Electrical's own language signal (the API has no separate "interface language" call).
        /// Null when no project is open or the call fails.
        /// </summary>
        public static string GetProjectLanguage()
        {
            if (App == null) return null;
            try
            {
                int err;
                var project = EwLate.CallOut(App, "getEwProjectCurrent", out err);
                if (project == null || !EwLate.Ok(err)) return null;
                var config = EwLate.CallOut(project, "getEwProjectConfiguration", out err);
                if (config == null || !EwLate.Ok(err)) return null;
                var code = EwLate.Str(EwLate.CallOut(config, "getCurrentCodeLanguage", out err));
                return code.Length > 0 && EwLate.Ok(err) ? code : null;
            }
            catch (Exception) { return null; }
        }

        public static ProjectRef GetActive()
        {
            if (App == null) return null;
            try
            {
                int err;
                var project = EwLate.CallOut(App, "getEwProjectCurrent", out err);
                if (project == null || !EwLate.Ok(err)) return null;
                return Describe(project, true);
            }
            catch (Exception ex)
            {
                Log.Error("Active project not available", ex);
                return null;
            }
        }

        /// <summary>Open projects, active one first.</summary>
        public static List<ProjectRef> ListOpen()
        {
            var list = new List<ProjectRef>();
            var active = GetActive();
            if (active != null) list.Add(active);
            if (App == null) return list;

            try
            {
                int err;
                var env = EwLate.CallOut(App, "getEwEnvironment", out err);
                if (env == null || !EwLate.Ok(err)) return list;
                var manager = EwLate.CallOut(env, "getEwProjectManager", out err);
                if (manager == null || !EwLate.Ok(err)) return list;
                var projects = EwLate.CallOut(manager, "getEwProjectArray", out err) as Array;
                if (projects == null) return list;

                foreach (object p in projects)
                {
                    if (p == null) continue;
                    int id = Convert.ToInt32(EwLate.Call(p, "getID"));
                    if (active != null && id == active.Id) continue;
                    bool open = Convert.ToBoolean(EwLate.CallOut(App, "isEwProjectOpened", out err, id));
                    if (open && EwLate.Ok(err)) list.Add(Describe(p, false));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not list open projects", ex);
            }
            return list;
        }

        static ProjectRef Describe(object project, bool active)
        {
            int err;
            return new ProjectRef
            {
                Id = Convert.ToInt32(EwLate.Call(project, "getID")),
                Name = EwLate.Str(EwLate.CallOut(project, "getName", out err)),
                IsActive = active
            };
        }

        /// <summary>Reads the parts of a project. projectId &lt;= 0 means the active project.</summary>
        public static ProjectSnapshot Read(int projectId)
        {
            var snap = new ProjectSnapshot();
            try
            {
                int err;
                object project = null;
                var active = GetActive();
                if (projectId <= 0 || (active != null && active.Id == projectId))
                {
                    project = EwLate.CallOut(App, "getEwProjectCurrent", out err);
                    snap.Project = active;
                }
                else
                {
                    var env = EwLate.CallOut(App, "getEwEnvironment", out err);
                    var manager = EwLate.CallOut(env, "getEwProjectManager", out err);
                    project = EwLate.CallOut(manager, "findEwProjectByID", out err, projectId);
                    if (project != null) snap.Project = Describe(project, false);
                }
                if (project == null || snap.Project == null) { snap.Warning = Loc.T("No SOLIDWORKS Electrical project is open."); return snap; }

                var partManager = EwLate.CallOut(project, "getEwProjectManufacturerPartManager", out err);
                var array = partManager == null ? null : EwLate.CallOut(partManager, "getEwProjectManufacturerPartArray", out err) as Array;
                if (array == null) return snap;

                var languages = CandidateLanguages(project);
                int described = 0, total = 0;
                var byKey = new Dictionary<string, ProjectPart>();
                foreach (object o in array)
                {
                    if (o == null) continue;
                    var reference = EwLate.Str(EwLate.CallOut(o, "getReference", out err));
                    if (reference.Length == 0) continue;
                    var manufacturer = EwLate.Str(EwLate.CallOut(o, "getManufacturer", out err));
                    var key = Key(manufacturer, reference);
                    if (byKey.ContainsKey(key)) continue;

                    var part = new ProjectPart
                    {
                        Reference = reference,
                        Manufacturer = manufacturer,
                        Description = ReadDescription(o, languages),
                        Supplier = EwLate.Str(EwLate.CallOut(o, "getSupplierName", out err)),
                        StockNumber = EwLate.Str(EwLate.CallOut(o, "getStockNumber", out err))
                    };
                    byKey[key] = part;
                    snap.Parts.Add(part);
                    total++;
                    if (part.Description.Length > 0) described++;
                }

                if (total > 0 && described == 0)
                    Log.Error("No part description could be read. Language codes tried: " + string.Join(", ", languages.Select(l => l.Length == 0 ? "(blank)" : l)) + ". Parts may simply have no description.");
                else if (total > 0)
                    Log.Info("Part descriptions read for " + described + " of " + total + " parts using language code \"" + _workingLanguage + "\"");

                CountUsage(project, snap, byKey);
            }
            catch (Exception ex)
            {
                Log.Error("Reading project parts failed", ex);
                snap.Warning = Loc.F("Could not read the project's parts: {0}", ex.Message);
            }
            return snap;
        }

        /// <summary>
        /// Language codes to try for descriptions, best first: the one that worked last time, the project's current language,
        /// the languages configured in the application, then common guesses.
        /// </summary>
        static List<string> CandidateLanguages(object project)
        {
            var list = new List<string>();
            Action<string> add = code => { if (code != null && !list.Contains(code)) list.Add(code); };
            add(_workingLanguage);

            try
            {
                int err;
                var config = EwLate.CallOut(project, "getEwProjectConfiguration", out err);
                if (config != null && EwLate.Ok(err))
                {
                    var current = EwLate.Str(EwLate.CallOut(config, "getCurrentCodeLanguage", out err));
                    if (current.Length > 0 && EwLate.Ok(err)) add(current);
                }
            }
            catch (Exception ex) { Log.Info("Project language not available: " + ex.Message); }

            try
            {
                int err;
                var settings = EwLate.CallOut(App, "getEwApplicationSettings", out err);
                var array = settings == null ? null : EwLate.CallOut(settings, "getLanguageArray", out err) as Array;
                if (array != null)
                    foreach (object o in array)
                    {
                        var code = EwLate.Str(o);
                        if (code.Length > 0) add(code);
                    }
            }
            catch (Exception ex) { Log.Info("Configured languages not available: " + ex.Message); }

            foreach (var code in FallbackLanguages) add(code);
            Log.Info("Description language candidates: " + string.Join(", ", list.Select(l => l.Length == 0 ? "(blank)" : l)));
            return list;
        }

        static string ReadDescription(object part, List<string> languages)
        {
            foreach (var language in languages)
            {
                try
                {
                    int err;
                    var text = EwLate.Str(EwLate.CallOut(part, "getDescription", out err, language));
                    if (text.Length > 0) { _workingLanguage = language; return text; }
                }
                catch (Exception) { /* try the next language code */ }
            }
            return "";
        }

        /// <summary>Quantity = number of project components that use the part. Bounded by a time budget; unknown (-1) when exceeded.</summary>
        static void CountUsage(object project, ProjectSnapshot snap, Dictionary<string, ProjectPart> byKey)
        {
            try
            {
                int err;
                var componentManager = EwLate.CallOut(project, "getEwProjectComponentManager", out err);
                var components = componentManager == null ? null : EwLate.CallOut(componentManager, "getEwProjectComponentArray", out err) as Array;
                if (components == null) return;

                var counts = new Dictionary<string, int>();
                var clock = Stopwatch.StartNew();
                foreach (object component in components)
                {
                    if (clock.ElapsedMilliseconds > CountBudgetMs) { snap.QuantitiesComplete = false; return; }
                    if (component == null) continue;
                    var used = EwLate.CallOut(component, "getEwProjectManufacturerPartArray", out err) as Array;
                    if (used == null) continue;
                    foreach (object p in used)
                    {
                        if (p == null) continue;
                        var key = Key(EwLate.Str(EwLate.CallOut(p, "getManufacturer", out err)), EwLate.Str(EwLate.CallOut(p, "getReference", out err)));
                        int n;
                        counts.TryGetValue(key, out n);
                        counts[key] = n + 1;
                    }
                }

                foreach (var kv in byKey)
                {
                    int n;
                    kv.Value.Quantity = counts.TryGetValue(kv.Key, out n) ? n : 0;
                }
                snap.QuantitiesComplete = true;
            }
            catch (Exception ex)
            {
                Log.Error("Counting part usage failed (quantities left blank)", ex);
            }
        }

        static string Key(string manufacturer, string reference)
        {
            return MatchFilter.Norm(manufacturer) + "|" + MatchFilter.Norm(reference);
        }
    }
}
