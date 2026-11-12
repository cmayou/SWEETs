namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>All tunable thresholds in one place (millimetres). Defaults validated on CAD-exported A3 schematics.</summary>
    public sealed class RecognitionOptions
    {
        /// <summary>Endpoint / T-junction snapping tolerance.</summary>
        public double ConnectTolerance { get; set; } = 0.35;
        /// <summary>Nets shorter than this are treated as symbol linework (actuators, ticks).</summary>
        public double MinNetLength { get; set; } = 8.0;
        /// <summary>Linework closer than this joins the same symbol cluster.</summary>
        public double ClusterGap { get; set; } = 0.8;
        /// <summary>A free wire end this close to a cluster's bounds becomes one of its pins.</summary>
        public double PinCapture { get; set; } = 0.6;
        /// <summary>Leaf stubs up to this length that re-enter the same symbol are pruned into it (NC ticks).</summary>
        public double StubMaxLength { get; set; } = 6.0;
        /// <summary>Largest air gap between the two halves of an open contact drawn as separate pieces.</summary>
        public double MaxContactGap { get; set; } = 6.0;
        /// <summary>Pin numbers ("13", "A1", "T2") are printed within this distance of the pin.</summary>
        public double PinLabelRadius { get; set; } = 3.0;
        public double JunctionDotMin { get; set; } = 0.5;
        public double JunctionDotMax { get; set; } = 3.0;
        public double TagRadius { get; set; } = 10.0;
        public double XrefRadius { get; set; } = 8.0;
        public double WireNumberRadius { get; set; } = 2.5;
        public double LabelRadius { get; set; } = 6.0;
        /// <summary>Max spacing between poles of one multi-pole device drawn as separate symbols.</summary>
        public double PoleSpacingMax { get; set; } = 25.0;
        /// <summary>Signature similarity required to reuse a learned mapping from a different-but-close shape.</summary>
        public double SimilarMatchThreshold { get; set; } = 0.86;
        /// <summary>Symbols at or above this confidence are pre-confirmed (still editable in review).</summary>
        public double AutoConfirmThreshold { get; set; } = 0.85;
        /// <summary>Fraction of frame height treated as border band (column numbering) when a full-width divider is found.</summary>
        public double BorderBandFraction { get; set; } = 0.12;

        /// <summary>Title blocks drawn as a full-width rule may sit up to this fraction of the frame height above the bottom edge.</summary>
        public double TitleBandFraction { get; set; } = 0.18;

        /// <summary>Longest dashed line treated as a mechanical link between poles (breaker, contactor, switch).</summary>
        public double MechanicalLinkMaxLength { get; set; } = 45.0;
    }
}
