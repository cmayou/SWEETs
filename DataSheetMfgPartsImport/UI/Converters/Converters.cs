using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DataSheetMfgPartsImport.UI.ViewModels;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.UI.Converters;

/// <summary>Looks up a theme brush by key so colours stay defined only in Theme.xaml.</summary>
internal static class ThemeBrush
{
    public static Brush Get(string key) =>
        Application.Current?.TryFindResource(key) as Brush
        ?? ThemeHost.Resources?[key] as Brush
        ?? Brushes.Gray;
}

/// <summary>Holds Theme.xaml when SOLIDWORKS Electrical has no WPF Application object.</summary>
public static class ThemeHost
{
    public static ResourceDictionary Resources { get; set; }
}

/// <summary>
/// Maps confidence to a short Unicode shape so the indicator works without colour (accessibility).
/// ● filled circle = found with a label, ○ ring = inferred, ▲ triangle = low confidence / guess, ✎ pencil = edited.
/// </summary>
public sealed class ConfidenceToShapeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is Confidence conf
        ? conf switch
        {
            Confidence.High   => "●",
            Confidence.Medium => "◐",
            Confidence.Low    => "▲",
            Confidence.Edited => "✎",
            _                 => "○",
        } : "○";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Tooltip text for a confidence dot so screen-reader and keyboard users get the same information.</summary>
public sealed class ConfidenceToTipConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is Confidence conf
        ? conf switch
        {
            Confidence.High   => "Found with a label in the data sheet",
            Confidence.Medium => "Inferred — worth checking",
            Confidence.Low    => "Low confidence guess — please verify",
            Confidence.Edited => "Edited by you",
            _                 => "Not found",
        } : "Not found";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class ConfidenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is Confidence conf
        ? conf switch
        {
            Confidence.High => ThemeBrush.Get("SuccessBrush"),
            Confidence.Medium => ThemeBrush.Get("WarningBrush"),
            Confidence.Low => ThemeBrush.Get("CautionBrush"),
            Confidence.Edited => ThemeBrush.Get("AccentBrush"),
            _ => ThemeBrush.Get("BorderStrongBrush"),
        }
        : ThemeBrush.Get("BorderStrongBrush");

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class CardStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is CardState s
        ? s switch
        {
            CardState.Created or CardState.Updated => ThemeBrush.Get("SuccessBrush"),
            CardState.Skipped => ThemeBrush.Get("TextMutedBrush"),
            CardState.Failed => ThemeBrush.Get("DangerBrush"),
            CardState.NeedsAttention => ThemeBrush.Get("WarningBrush"),
            CardState.Building => ThemeBrush.Get("AccentBrush"),
            _ => ThemeBrush.Get("AccentBrush"),
        }
        : ThemeBrush.Get("AccentBrush");

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>"info|success|warning|error" or step "pending|active|done|error" to a brush.</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => (value as string) switch
    {
        "success" or "done" => ThemeBrush.Get("SuccessBrush"),
        "warning" => ThemeBrush.Get("WarningBrush"),
        "error" => ThemeBrush.Get("DangerBrush"),
        "active" => ThemeBrush.Get("AccentBrush"),
        "pending" => ThemeBrush.Get("BorderStrongBrush"),
        _ => ThemeBrush.Get("TextMutedBrush"),
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var b = value switch
        {
            bool x => x,
            int i => i != 0,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Percent (0-100) to a pixel width of the bar's track (ConverterParameter = track width).</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var pct = value is int i ? i : value is double d ? d : 0;
        var width = double.TryParse(p as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100;
        return Math.Max(2, width * Math.Min(100, Math.Max(0, pct)) / 100.0);
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
