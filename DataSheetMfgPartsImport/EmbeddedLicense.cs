namespace DataSheetMfgPartsImport
{
    /// <summary>
    /// Not used inside SWEETs: the SWEETs host connects to SOLIDWORKS Electrical with its own key
    /// (src\SWEETs.AddIn\Secrets). The key that used to be here was removed before the code went to GitHub.
    /// A non-empty LicenseCode in settings.json still overrides the empty value.
    /// </summary>
    internal static class EmbeddedLicense
    {
        public const string Code = "";

        public static string Resolve(string overrideFromSettings)
        {
            return string.IsNullOrWhiteSpace(overrideFromSettings) ? Code : overrideFromSettings.Trim();
        }
    }
}
