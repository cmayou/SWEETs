namespace SWEETs.AddIn.Secrets
{
    /// <summary>
    /// TEMPLATE - committed to git, holds no key.
    /// To build a working add-in: copy this file to SweApiKeys.local.cs in this folder (git ignores that name),
    /// then put your SOLIDWORKS Electrical API key(s) in Values. When SweApiKeys.local.cs exists the build uses it
    /// and leaves this file out. Without it the add-in builds, but SOLIDWORKS Electrical will refuse the connection.
    /// </summary>
    internal static class SweApiKeys
    {
        public static readonly string[] Values =
        {
            // "YOUR-SOLIDWORKS-ELECTRICAL-API-KEY",
        };
    }
}
