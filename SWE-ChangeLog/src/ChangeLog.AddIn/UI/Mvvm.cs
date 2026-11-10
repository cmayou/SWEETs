using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ChangeLog.Core.Model;

namespace ChangeLog.AddIn.UI
{
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _run;
        private readonly Func<object, bool> _can;

        public RelayCommand(Action run, Func<bool> can = null) : this(_ => run(), can == null ? (Func<object, bool>)null : _ => can()) { }

        public RelayCommand(Action<object> run, Func<object, bool> can = null)
        {
            _run = run;
            _can = can;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object p) => _can == null || _can(p);
        public void Execute(object p) => _run(p);
    }

    /// <summary>Semantic colours shared by views, charts and the page canvas.</summary>
    public static class Palette
    {
        // SWEETs palette (src\SWEETs.UI\Theme.xaml): Success, Warning, Danger, Accent, BorderStrong, Ink.
        public static readonly Color Added = Color.FromRgb(0x04, 0x78, 0x57);
        public static readonly Color Edited = Color.FromRgb(0xB4, 0x53, 0x09);
        public static readonly Color Removed = Color.FromRgb(0xB9, 0x1C, 0x1C);
        public static readonly Color Accent = Color.FromRgb(0x1D, 0x4E, 0xD8);
        public static readonly Color Unchanged = Color.FromRgb(0xB8, 0xBD, 0xC7);
        public static readonly Color Text = Color.FromRgb(0x11, 0x18, 0x27);

        public static Color For(ChangeAction a)
        {
            switch (a)
            {
                case ChangeAction.Added: return Added;
                case ChangeAction.Removed: return Removed;
                default: return Edited;
            }
        }

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static SolidColorBrush Soft(Color c)
        {
            var b = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }

    /// <summary>ChangeAction → foreground brush (parameter "soft" → tinted background).</summary>
    public sealed class ActionBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is ChangeAction)) return Brushes.Transparent;
            var c = Palette.For((ChangeAction)value);
            return (parameter as string) == "soft" ? Palette.Soft(c) : Palette.Brush(c);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool && (bool)value;
            if (value is int) b = (int)value != 0;
            if (value is string) b = !string.IsNullOrEmpty((string)value);
            if (value != null && !(value is bool) && !(value is int) && !(value is string)) b = true;
            return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
