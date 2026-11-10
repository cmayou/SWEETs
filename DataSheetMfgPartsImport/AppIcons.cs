using System.IO;

namespace DataSheetMfgPartsImport
{
    /// <summary>The add-in's icon files (Icons\import16.png, import32.png, import64.png), installed next to the add-in.</summary>
    internal static class AppIcons
    {
        public static string Dir
        {
            get { return Path.Combine(Path.GetDirectoryName(typeof(AppIcons).Assembly.Location) ?? "", "Icons"); }
        }

        public static string PathOf(string fileName)
        {
            return Path.Combine(Dir, fileName);
        }
    }
}
