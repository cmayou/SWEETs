namespace ChangeLog.Core.Model
{
    /// <summary>What happened to an object.</summary>
    public enum ChangeAction
    {
        Added = 0,
        Edited = 1,
        Removed = 2
    }

    /// <summary>Project object categories tracked by the log. Values are persisted; never renumber.</summary>
    public enum ObjectKind
    {
        Other = 0,
        Project = 1,
        Page = 2,
        Symbol = 3,
        Wire = 4,
        WireSegment = 5,
        Cable = 6,
        ManufacturerPart = 7,
        Component = 8,
        Text = 9,
        Location = 10,
        Function = 11,
        Harness = 12
    }

    /// <summary>How the record was produced.</summary>
    public enum ChangeSource
    {
        /// <summary>Captured from a SOLIDWORKS Electrical event while the add-in was running.</summary>
        Live = 0,

        /// <summary>Detected by comparing project state on open (change made without the add-in running).</summary>
        Reconciled = 1
    }

    /// <summary>Confidence of the "who" field.</summary>
    public enum Attribution
    {
        /// <summary>The user was running the session that raised the event.</summary>
        Confirmed = 0,

        /// <summary>Best guess (e.g. project "modified by"); the change was not observed live.</summary>
        Inferred = 1
    }

    public enum BaselineKind
    {
        Manual = 0,
        SessionStart = 1,
        SessionEnd = 2,
        NativeSnapshot = 3
    }

    public enum GeometryShape
    {
        None = 0,
        Point = 1,
        Rect = 2,
        Segment = 3
    }
}
