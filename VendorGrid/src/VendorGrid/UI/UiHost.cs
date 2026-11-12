using System;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    /// <summary>
    /// Owns the single WPF window inside the SOLIDWORKS Electrical process.
    /// Everything here must run on the host's UI thread (commands are executed there).
    /// </summary>
    internal static class UiHost
    {
        static MainWindow _window;

        public static Func<IntPtr> OwnerHandleProvider { get; set; }

        public static void ShowMainWindow()
        {
            EnsureApplication();

            if (_window != null)
            {
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _window.Activate();
                return;
            }

            _window = new MainWindow();
            _window.Closed += (s, e) => _window = null;

            var helper = new WindowInteropHelper(_window);
            var owner = IntPtr.Zero;
            try { owner = OwnerHandleProvider != null ? OwnerHandleProvider() : IntPtr.Zero; } catch (Exception ex) { Log.Error("Owner handle unavailable", ex); }
            if (owner != IntPtr.Zero) helper.Owner = owner;

            // Lets keyboard input (typing, Tab, Enter) reach the WPF window while the native host owns the message loop.
            ElementHost.EnableModelessKeyboardInterop(_window);
            _window.Show();
            _window.Activate();
        }

        public static void Shutdown()
        {
            try { if (_window != null) _window.Close(); } catch { }
            _window = null;
        }

        static void EnsureApplication()
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };   // registers pack:// URIs

            var dicts = Application.Current.Resources.MergedDictionaries;
            const string theme = "pack://application:,,,/VendorGrid;component/UI/Theme.xaml";
            foreach (var d in dicts) if (d.Source != null && d.Source.OriginalString == theme) return;
            dicts.Add(new ResourceDictionary { Source = new Uri(theme, UriKind.Absolute) });
        }
    }
}
