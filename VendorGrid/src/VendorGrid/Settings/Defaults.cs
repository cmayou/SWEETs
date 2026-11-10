namespace VendorGrid.Settings
{
    public static class Defaults
    {
        // The SOLIDWORKS Electrical API key is held by the SWEETs host, which connects for every SWEETs tool.

        /// <summary>CLSID of the stand-alone VendorGrid add-in; the SWEETs installer removes that registration.</summary>
        public const string AddInGuid = "101df681-3a3a-4ce5-a370-4bef5a8ed4be";
        public const string AddInTitle = "VendorGrid";
        public const string AddInDescription = "Compare availability, vendor SKUs and pricing for a part number across vendor websites.";
        public const string CommandName = "VendorGridSearch";
        public const string Version = "2.0.0";
    }
}
