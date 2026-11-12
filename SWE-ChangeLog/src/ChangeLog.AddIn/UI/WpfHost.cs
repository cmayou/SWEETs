using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ChangeLog.AddIn.Ew;
using ChangeLog.AddIn.Host;
using EwAPI;

namespace ChangeLog.AddIn.UI
{
    /// <summary>WPF plumbing for running inside the native (MFC) SWE process.</summary>
    internal static class WpfHost
    {
        /// <summary>
        /// pack:// resource URIs and Application-level services need an Application object.
        /// SWE has none, so create one that never shuts down on its own. Never call Run().
        /// </summary>
        public static void EnsureApplication()
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        public static IntPtr MainWindowHandle => Process.GetCurrentProcess().MainWindowHandle;

        public static void SetOwner(Window w)
        {
            var h = MainWindowHandle;
            if (h != IntPtr.Zero) new WindowInteropHelper(w).Owner = h;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>
    /// Hosts a WPF element inside a native SWE docking pane: the element lives in an
    /// HwndSource child window that the pane re-parents via IEwDockingPaneX.attach().
    /// </summary>
    internal sealed class PaneHost : IDisposable
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WM_GETDLGCODE = 0x0087;
        private const int DLGC_WANTARROWS = 0x0001;
        private const int DLGC_WANTTAB = 0x0002;
        private const int DLGC_WANTALLKEYS = 0x0004;
        private const int DLGC_HASSETSEL = 0x0008;
        private const int DLGC_WANTCHARS = 0x0080;

        private readonly IEwDockingPaneX _pane;
        private readonly HwndSource _source;

        public PaneHost(IEwApplicationX app, UIElement content, string caption, string iconPath)
        {
            EwErrorCode e;
            var mgr = app.getEwDockingPaneManager(out e);
            Com.Check(e, "getEwDockingPaneManager");
            _pane = mgr.addPane(caption, EwDockingPaneDirection.EwPaneDockRight, out e);
            Com.Check(e, "addPane");
            if (File.Exists(iconPath)) _pane.setIconFromImage(iconPath, 0x00FF00FF);

            var p = new HwndSourceParameters("ChangeLogPane")
            {
                ParentWindow = WpfHost.MainWindowHandle,
                WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                Width = 520,
                Height = 760
            };
            _source = new HwndSource(p) { SizeToContent = SizeToContent.Manual };
            _source.CompositionTarget.BackgroundColor = Color.FromRgb(0xF5, 0xF6, 0xF8);
            _source.AddHook(Hook);
            _source.RootVisual = (Visual)content;
            Com.Check(_pane.attach((ulong)_source.Handle.ToInt64()), "pane.attach");
        }

        public bool IsAlive
        {
            get
            {
                if (_source.IsDisposed) return false;
                EwErrorCode e;
                return Com.Try(() => _pane.isValid(out e) && !_pane.isClosed(out e));
            }
        }

        public void Show()
        {
            var r = _pane.show(true);
            if (r != EwErrorCode.EW_NO_ERROR) Log.Warn("pane.show: " + r);
        }

        /// <summary>
        /// MFC treats the pane like a dialog and would swallow arrows, Tab and characters.
        /// Claim all keys so TextBox, DataGrid navigation and shortcuts work.
        /// </summary>
        private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_GETDLGCODE) return IntPtr.Zero;
            handled = true;
            return new IntPtr(DLGC_WANTALLKEYS | DLGC_WANTARROWS | DLGC_WANTTAB | DLGC_WANTCHARS | DLGC_HASSETSEL);
        }

        public void Dispose()
        {
            try
            {
                _source.RootVisual = null; // lets the view be re-hosted in a new pane later
                _source.RemoveHook(Hook);
                _source.Dispose();
                _pane.remove();
            }
            catch (Exception ex)
            {
                Log.Warn("Pane cleanup failed: " + ex.Message);
            }
        }
    }

    /// <summary>Small transient notification shown when the pane is not visible.</summary>
    internal static class Toast
    {
        public static void Show(string message, NotificationKind kind)
        {
            var bg = kind == NotificationKind.Error ? Palette.Removed
                : kind == NotificationKind.Warning ? Palette.Edited
                : kind == NotificationKind.Success ? Palette.Added : Palette.Text;
            var text = new System.Windows.Controls.TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12
            };
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                Content = new System.Windows.Controls.Border
                {
                    Background = Palette.Brush(bg),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(10),
                    Child = text,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 }
                }
            };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(text, System.Windows.Automation.AutomationLiveSetting.Assertive);
            WpfHost.SetOwner(w);
            w.Loaded += (s, e) =>
            {
                var area = SystemParameters.WorkArea;
                w.Left = area.Right - w.ActualWidth - 16;
                w.Top = area.Bottom - w.ActualHeight - 16;
            };
            w.MouseLeftButtonUp += (s, e) => w.Close();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == NotificationKind.Error ? 10 : 5) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                w.Close();
            };
            w.Show();
            timer.Start();
        }
    }
}
