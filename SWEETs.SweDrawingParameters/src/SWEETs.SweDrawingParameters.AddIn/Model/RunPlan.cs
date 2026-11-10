using System.Collections.Generic;
using SWEETs.SweDrawingParameters.AddIn.Services;

namespace SWEETs.SweDrawingParameters.AddIn.Model
{
    /// <summary>What the dialog asked for. Executed by the run window after the dialog has closed.</summary>
    public sealed class RunPlan
    {
        public string ProjectName;
        public List<DrawingInfo> Drawings = new List<DrawingInfo>();
        public DrawingParameters Request;
    }
}
