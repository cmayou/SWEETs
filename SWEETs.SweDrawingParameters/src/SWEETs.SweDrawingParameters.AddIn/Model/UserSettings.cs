using System;
using System.IO;
using System.Xml.Serialization;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;

namespace SWEETs.SweDrawingParameters.AddIn.Model
{
    /// <summary>Last values used in the dialog (%AppData%\SWEETs\DrawingParameters\settings.xml).</summary>
    public sealed class UserSettings
    {
        public bool HasValues { get; set; }

        public bool ApplySnap { get; set; } = true;
        public bool SnapOn { get; set; } = true;
        public string SnapSpacing { get; set; }

        public bool ApplyGrid { get; set; } = true;
        public bool GridOn { get; set; } = true;
        public string GridSpacing { get; set; }

        public bool ApplyZone { get; set; }
        public string ZoneMinX { get; set; }
        public string ZoneMinY { get; set; }
        public string ZoneMaxX { get; set; }
        public string ZoneMaxY { get; set; }

        public bool ApplyOrtho { get; set; }
        public bool OrthoOn { get; set; }



        private static string FilePath => Path.Combine(AppPaths.RoamingRoot, "settings.xml");

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var s = File.OpenRead(FilePath))
                        return (UserSettings)new XmlSerializer(typeof(UserSettings)).Deserialize(s);
            }
            catch (Exception ex) { FileLog.Write("settings", "Settings unreadable, using defaults: " + ex.Message); }
            return new UserSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var s = File.Create(FilePath))
                    new XmlSerializer(typeof(UserSettings)).Serialize(s, this);
            }
            catch (Exception ex) { FileLog.Write("settings", "Settings not saved: " + ex.Message); }
        }
    }
}
