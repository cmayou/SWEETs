using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SWEETs.SwePdfSchematic.AddIn.UI
{
    /// <summary>bool → Visibility. ConverterParameter="Invert" flips it.</summary>
    public sealed class BoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            bool b = value is bool v && v;
            if ((p as string) == "Invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Enum ↔ bool for RadioButton groups: IsChecked="{Binding Filter, Converter={StaticResource EnumEq}, ConverterParameter=Confirmed}".</summary>
    public sealed class EnumEquals : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value != null && value.ToString() == (p as string);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
            value is bool b && b ? Enum.Parse(t, (string)p) : Binding.DoNothing;
    }

    /// <summary>Visible when the bound enum equals the parameter.</summary>
    public sealed class EnumToVisibility : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value != null && value.ToString() == (p as string) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>null / empty collection / empty string → Collapsed. ConverterParameter="Invert" flips it.</summary>
    public sealed class HasValueToVisibility : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            bool has = value != null && !(value is string s && s.Length == 0) && !(value is ICollection col && col.Count == 0) && !(value is int i && i == 0);
            if ((p as string) == "Invert") has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class MillimetresText : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? (d <= 0 ? "Off" : $"{d:0.##} mm") : value;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
