using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using SWEETs.AddIn.Entry;
using SWEETs.AddIn.Infrastructure;
using SWEETs.AddIn.Settings;

namespace SWEETs.AddIn.UI
{
    /// <summary>One row of the Settings window.</summary>
    public sealed class ToolRow
    {
        public string Id { get; set; }
        public string Caption { get; set; }
        public string Description { get; set; }
        public bool IsOn { get; set; }
        /// <summary>Loaded, Failed or Off (as SOLIDWORKS Electrical currently has it, before saving).</summary>
        public string State { get; set; }
        public string StateText { get; set; }
        public string Error { get; set; }
    }

    /// <summary>One ribbon group in the Settings window.</summary>
    public sealed class ToolGroupRow
    {
        public string Caption { get; set; }
        public List<ToolRow> Rows { get; set; }
    }

    public partial class SettingsWindow : Window
    {
        private readonly List<ToolDefinition> _tools;
        private readonly SweetsSettings _settings;
        private readonly List<ToolRow> _rows;

        /// <summary>The saved settings, or null when the window was cancelled.</summary>
        public SweetsSettings Result { get; private set; }

        public SettingsWindow(List<ToolDefinition> tools, SweetsSettings settings)
        {
            InitializeComponent();
            _tools = tools ?? new List<ToolDefinition>();
            _settings = settings ?? new SweetsSettings();

            _rows = _tools.Select(t => new ToolRow
            {
                Id = t.Id,
                Caption = t.Caption,
                Description = t.Description,
                IsOn = _settings.IsEnabled(t.Id),
                State = t.Connected ? "Loaded" : t.LastError != null ? "Failed" : "Off",
                StateText = t.Connected ? "Loaded" : t.LastError != null ? "Not loaded" : "Off",
                Error = t.Connected ? null : t.LastError,
            }).ToList();

            GroupsList.ItemsSource = ToolCatalog.Groups
                .Select(g => new ToolGroupRow { Caption = g, Rows = Rows(g) })
                .Where(g => g.Rows.Count > 0)
                .ToList();
            VersionText.Text = "SWEETs " + typeof(SettingsWindow).Assembly.GetName().Version.ToString(3);
        }

        private List<ToolRow> Rows(string group) =>
            _rows.Where(r => _tools.First(t => t.Id == r.Id).Group == group).ToList();

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var result = _settings.Clone();
            foreach (var row in _rows) result.SetEnabled(row.Id, row.IsOn);
            try
            {
                result.Save(_tools.Select(t => t.Id));
                FileLog.Write("settings", "Saved: " + string.Join(", ", _rows.Select(r => r.Id + "=" + (r.IsOn ? "on" : "off"))));
            }
            catch (Exception ex)
            {
                FileLog.Write("settings", ex);
                MessageBox.Show(this, "The settings could not be saved: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Result = result;
            DialogResult = true;
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(FileLog.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + FileLog.Folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
