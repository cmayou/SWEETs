using System;
using System.Collections.Generic;
using System.Globalization;

namespace SWEETs.SweDrawingParameters.AddIn.Model
{
    /// <summary>
    /// The drawing parameters this tool manages, as SOLIDWORKS Electrical stores them in each drawing (.ewg = DWG):
    /// <list type="bullet">
    /// <item>Snap on/off and snap spacing → the "*Active" VPORT table record (SNAPMODE, SNAPUNIT).</item>
    /// <item>Grid on/off and grid spacing → the "*Active" VPORT table record (GRIDMODE, GRIDUNIT).</item>
    /// <item>Grid zone → the drawing header limits (LIMMIN, LIMMAX).</item>
    /// <item>Ortho → the drawing header (ORTHOMODE).</item>
    /// </list>
    /// Object snap modes (OSMODE) are NOT stored in DWG files from R15 (AutoCAD 2000) on; they are an application
    /// setting of SOLIDWORKS Electrical, so this tool does not change them.
    /// A null value means "unknown" when read, and "leave unchanged" in an update request.
    /// </summary>
    public sealed class DrawingParameters
    {
        public bool? SnapOn { get; set; }
        public double? SnapX { get; set; }
        public double? SnapY { get; set; }
        public bool? GridOn { get; set; }
        public double? GridX { get; set; }
        public double? GridY { get; set; }
        public double? ZoneMinX { get; set; }
        public double? ZoneMinY { get; set; }
        public double? ZoneMaxX { get; set; }
        public double? ZoneMaxY { get; set; }
        public bool? OrthoOn { get; set; }

        public bool HasAnyValue =>
            SnapOn.HasValue || SnapX.HasValue || SnapY.HasValue || GridOn.HasValue || GridX.HasValue || GridY.HasValue ||
            ZoneMinX.HasValue || ZoneMinY.HasValue || ZoneMaxX.HasValue || ZoneMaxY.HasValue || OrthoOn.HasValue;

        /// <summary>Requested values that differ from <paramref name="actual"/> (tolerance 1e-9), as readable text.</summary>
        public List<string> Mismatches(DrawingParameters actual)
        {
            var list = new List<string>();
            void Flag(string name, bool? want, bool? got) { if (want.HasValue && want != got) list.Add($"{name}: expected {OnOff(want)}, file has {OnOff(got)}"); }
            void Num(string name, double? want, double? got)
            {
                if (want.HasValue && (!got.HasValue || Math.Abs(want.Value - got.Value) > 1e-9))
                    list.Add($"{name}: expected {Fmt(want)}, file has {Fmt(got)}");
            }
            Flag("Snap", SnapOn, actual.SnapOn);
            Num("Snap X", SnapX, actual.SnapX);
            Num("Snap Y", SnapY, actual.SnapY);
            Flag("Grid", GridOn, actual.GridOn);
            Num("Grid X", GridX, actual.GridX);
            Num("Grid Y", GridY, actual.GridY);
            Num("Grid zone min X", ZoneMinX, actual.ZoneMinX);
            Num("Grid zone min Y", ZoneMinY, actual.ZoneMinY);
            Num("Grid zone max X", ZoneMaxX, actual.ZoneMaxX);
            Num("Grid zone max Y", ZoneMaxY, actual.ZoneMaxY);
            Flag("Ortho", OrthoOn, actual.OrthoOn);
            return list;
        }

        public static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.######", CultureInfo.InvariantCulture) : "—";
        public static string OnOff(bool? v) => v.HasValue ? (v.Value ? "On" : "Off") : "—";

        public string SnapText => $"{OnOff(SnapOn)} · {Pair(SnapX, SnapY)}";
        public string GridText => $"{OnOff(GridOn)} · {Pair(GridX, GridY)}";
        public string ZoneText => $"{Pair(ZoneMinX, ZoneMinY)} → {Pair(ZoneMaxX, ZoneMaxY)}";

        private static string Pair(double? x, double? y) =>
            x.HasValue && y.HasValue && Math.Abs(x.Value - y.Value) < 1e-12 ? Fmt(x) : $"{Fmt(x)}, {Fmt(y)}";
    }
}
