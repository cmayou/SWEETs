using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using SWEETs.SwePdfSchematic.Core.Model;
using C = SWEETs.SwePdfSchematic.AddIn.Interop.SweContract;

namespace SWEETs.SwePdfSchematic.AddIn.Interop
{
    /// <summary>A catalog entry found (or created) for a part from the PDF.</summary>
    public sealed class CatalogMatch
    {
        public object Item;
        public string Manufacturer;
        public string Reference;
        public bool Created;
    }

    /// <summary>
    /// The manufacturer part catalog (and cable references) of the connected SOLIDWORKS Electrical environment.
    /// Parts are matched on manufacturer + reference; when the PDF gives no manufacturer the reference alone is used.
    /// Missing parts are created (manufacturer, reference, description, article number) so they can be assigned.
    /// Must be used on SWE's UI thread.
    /// </summary>
    public sealed class SwePartCatalog
    {
        private const string Lang = "en";
        public const string UnknownManufacturer = "Unknown";
        private readonly object _parts;
        private readonly object _cables;
        private readonly Dictionary<string, CatalogMatch> _cache = new Dictionary<string, CatalogMatch>();

        public SwePartCatalog(object app)
        {
            var env = SweApi.Call(app, C.AppGetEnvironment);
            SweApi.TryCall(env, C.EnvGetPartManager, out _parts);
            SweApi.TryCall(env, C.EnvGetCableRefManager, out _cables);
        }

        public bool Available => _parts != null;

        // ------------------------------------------------------------------ lookup

        /// <summary>Finds a manufacturer part (or, for cables, a cable reference); null when the catalog has none.</summary>
        public CatalogMatch Find(string manufacturer, string reference, bool cable = false)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            var key = (cable ? "C|" : "P|") + (manufacturer ?? "").Trim().ToUpperInvariant() + "|" + BomText.Key(reference);
            if (_cache.TryGetValue(key, out var hit)) return hit;

            var mgr = cable ? _cables : _parts;
            CatalogMatch match = null;
            if (mgr != null)
            {
                if (!string.IsNullOrWhiteSpace(manufacturer)
                    && SweApi.TryCall(mgr, cable ? C.CableRefMgrFind : C.PartMgrFind, out var item, manufacturer.Trim(), reference.Trim()) && item != null)
                    match = Describe(item, cable);
                if (match == null && !cable) match = Search(manufacturer, reference);
            }
            if (match != null) _cache[key] = match;
            return match;
        }

        /// <summary>Reference filter (exact match ignoring spaces and punctuation), then manufacturer when the PDF gives one.</summary>
        private CatalogMatch Search(string manufacturer, string reference)
        {
            try
            {
                if (!SweApi.TryCall(_parts, C.PartMgrNewFilters, out var filters) || filters == null) return null;
                SweApi.Call(filters, C.PartFiltersSet, SweApi.EnumValue("EwManufacturerPartFilterType", "kPartFilterReference"), reference.Trim());
                if (!SweApi.TryCall(filters, C.PartFiltersGet, out var arr) || !(arr is IEnumerable items)) return null;
                var want = BomText.Key(reference);
                var found = new List<CatalogMatch>();
                foreach (var item in items)
                {
                    var m = Describe(item, false);
                    if (m != null && BomText.Key(m.Reference) == want) found.Add(m);
                }
                if (found.Count == 0) return null;
                if (!string.IsNullOrWhiteSpace(manufacturer))
                    return found.FirstOrDefault(m => string.Equals(m.Manufacturer?.Trim(), manufacturer.Trim(), StringComparison.OrdinalIgnoreCase));
                return found.Count == 1 ? found[0] : found.OrderBy(m => m.Manufacturer, StringComparer.OrdinalIgnoreCase).First();
            }
            catch (SweApiException ex)
            {
                FileLog.Write("parts", $"catalog search for \"{reference}\" failed: {ex.Message}");
                return null;
            }
        }

        private static CatalogMatch Describe(object item, bool cable)
        {
            if (item == null) return null;
            SweApi.TryCall(item, cable ? C.CableRefGetManufacturer : C.PartGetManufacturer, out var mfr);
            SweApi.TryCall(item, cable ? C.CableRefGetReference : C.PartGetReference, out var reference);
            return new CatalogMatch { Item = item, Manufacturer = mfr?.ToString(), Reference = reference?.ToString() };
        }

        /// <summary>Catalog state shown on the review cards.</summary>
        public void Check(PartAssignment p, bool cable = false)
        {
            try
            {
                var m = Find(p.Manufacturer, p.Reference, cable);
                p.Catalog = m != null ? CatalogState.InCatalog : CatalogState.WillCreate;
                p.CatalogNote = m != null
                    ? (string.IsNullOrWhiteSpace(p.Manufacturer) ? $"In catalog ({m.Manufacturer})" : "In catalog")
                    : "New — added to the catalog when the project is built";
            }
            catch (Exception ex)
            {
                p.Catalog = CatalogState.Unknown;
                p.CatalogNote = "Catalog not checked: " + ex.Message;
            }
        }

        // ------------------------------------------------------------------ create

        /// <summary>Finds the part, or creates it in the catalog. Throws <see cref="SweApiException"/> with the reason when it cannot.</summary>
        public CatalogMatch Ensure(string manufacturer, string reference, string description, string articleNumber, bool cable = false, bool accessory = false)
        {
            var found = Find(manufacturer, reference, cable);
            if (found != null) return found;
            var mgr = cable ? _cables : _parts;
            if (mgr == null) throw new SweApiException("the manufacturer part catalog is not available through the API.");

            var mfr = string.IsNullOrWhiteSpace(manufacturer) ? UnknownManufacturer : manufacturer.Trim();
            var item = SweApi.Call(mgr, cable ? C.CableRefMgrNew : C.PartMgrNew);
            SweApi.Call(item, cable ? C.CableRefSetManufacturer : C.PartSetManufacturer, mfr);
            SweApi.Call(item, cable ? C.CableRefSetReference : C.PartSetReference, reference.Trim());
            if (!string.IsNullOrWhiteSpace(description))
                SweApi.TryCall(item, cable ? C.CableRefSetDescription : C.PartSetDescription, out _, Lang, description.Trim());
            if (!string.IsNullOrWhiteSpace(articleNumber))
                SweApi.TryCall(item, cable ? C.CableRefSetArticleNumber : C.PartSetArticleNumber, out _, articleNumber.Trim());
            if (!cable)
                try { SweApi.Call(item, C.PartSetType, SweApi.EnumValue("EwManufacturerPartType", accessory ? "kManufacturerPartAccessory" : "kManufacturerPartBase")); } catch (SweApiException) { }
            SweApi.Call(item, C.ObjInsert);
            FileLog.Write("parts", $"created catalog {(cable ? "cable reference" : "part")} {mfr} / {reference}");

            var created = new CatalogMatch { Item = item, Manufacturer = mfr, Reference = reference.Trim(), Created = true };
            _cache[(cable ? "C|" : "P|") + (manufacturer ?? "").Trim().ToUpperInvariant() + "|" + BomText.Key(reference)] = created;
            _cache[(cable ? "C|" : "P|") + mfr.ToUpperInvariant() + "|" + BomText.Key(reference)] = created;
            return created;
        }
    }
}
