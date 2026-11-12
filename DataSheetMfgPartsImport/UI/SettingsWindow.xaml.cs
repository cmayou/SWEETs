using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DataSheetMfgPartsImport.Extraction;
using DataSheetMfgPartsImport.Model;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport.UI;

/// <summary>One row of the table that says where each extracted value is saved.</summary>
public sealed class MapRow
{
    public MapRow(string label, string key, string target)
    {
        Label = label;
        Key = key;
        TargetId = target;
    }

    public string Label { get; }
    public string Key { get; }
    public string TargetId { get; set; }
    public List<Target> Choices => Targets.All;
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<MapRow> _rows = new();

    public SettingsWindow()
    {
        InitializeComponent();

        EngineBox.SelectedIndex = string.Equals(_settings.Engine, AppSettings.EngineOllama, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UrlBox.Text = _settings.OllamaUrl;
        ModelBox.Text = _settings.OllamaModel;
        LibraryCode.Text = _settings.LibraryCode;
        LanguageBox.Text = _settings.Language;
        DatasheetFolder.Text = _settings.CopyDatasheetToFolder;
        SlotNote.Text = _settings.ImportNoteSlot.ToString();
        KeyStatus.Text = "The SOLIDWORKS Electrical API license is built into the add-in. Settings file: " + AppSettings.FilePath;

        foreach (var (field, key) in PartKeys.All)
        {
            if (PartKeys.IsFixed(field)) continue;
            var info = PartFieldInfo.Get(field);
            var label = string.IsNullOrEmpty(info.Unit) ? info.Label : $"{info.Label} ({info.Unit})";
            _rows.Add(new MapRow(label, key, _settings.MapTarget(key)));
        }
        MapGrid.ItemsSource = _rows;
        ShowEnginePanel();
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e) => ShowEnginePanel();

    private void ShowEnginePanel()
    {
        if (OllamaPanel == null || EngineBox == null) return;
        OllamaPanel.Visibility = EngineBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTestOllama(object sender, RoutedEventArgs e)
    {
        TestStatus.Text = "Contacting Ollama…";
        try
        {
            var models = await OllamaExtractor.ListModelsAsync(UrlBox.Text.Trim(), CancellationToken.None);
            ModelBox.ItemsSource = models;
            TestStatus.Text = models.Count == 0
                ? "Ollama answered, but no models are installed. Pull one, for example: ollama pull qwen2.5vl:7b"
                : $"{models.Count} model(s) found.";
        }
        catch (Exception ex)
        {
            TestStatus.Text = "Could not reach Ollama: " + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SlotNote.Text?.Trim(), out var note) || note < 0 || note > 20)
        {
            MessageBox.Show(this, "The user data number must be a whole number between 0 and 20.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Engine = EngineBox.SelectedIndex == 1 ? AppSettings.EngineOllama : AppSettings.EngineRules;
        _settings.OllamaUrl = UrlBox.Text.Trim();
        _settings.OllamaModel = (ModelBox.Text ?? "").Trim();
        _settings.LibraryCode = LibraryCode.Text.Trim();
        _settings.Language = LanguageBox.Text.Trim();
        _settings.CopyDatasheetToFolder = DatasheetFolder.Text.Trim();
        _settings.ImportNoteSlot = note;
        foreach (var row in _rows) _settings.Mapping[row.Key] = string.IsNullOrEmpty(row.TargetId) ? "none" : row.TargetId;
        _settings.Save();
        DialogResult = true;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Folder for data sheet copies",
            SelectedPath = DatasheetFolder.Text,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) DatasheetFolder.Text = dlg.SelectedPath;
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppSettings.FilePath)) _settings.Save();
        Process.Start(new ProcessStartInfo(AppSettings.FilePath) { UseShellExecute = true });
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Folder);
        Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true });
    }
}
