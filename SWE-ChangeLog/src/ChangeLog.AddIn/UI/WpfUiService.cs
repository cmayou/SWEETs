using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChangeLog.AddIn.Host;
using ChangeLog.Core.Export;
using ChangeLog.Core.Model;
using ChangeLog.Core.Util;
using Microsoft.Win32;

namespace ChangeLog.AddIn.UI
{
    /// <summary>WPF implementation of <see cref="IUiService"/>. All members run on the SWE UI thread.</summary>
    public sealed class WpfUiService : IUiService
    {
        private AddInHost _host;
        private ChangeLogViewModel _vm;
        private ChangeLogView _view;
        private PaneHost _pane;
        private CompareWindow _compare;
        private ProjectMonitor _compareMonitor;

        private static string Icon(string name) => Path.Combine(AddInHost.InstallFolder, "Resources", name);

        public void Initialize(AddInHost host)
        {
            _host = host;
            WpfHost.EnsureApplication();
        }

        public void ShowChangeLog()
        {
            if (_vm == null)
            {
                _vm = new ChangeLogViewModel(_host);
                _view = new ChangeLogView(_vm);
            }

            if (_pane == null || !_pane.IsAlive)
            {
                _pane?.Dispose();
                _pane = new PaneHost(_host.App, _view, AddInHost.TabCaption, Icon("changelog_16.png"));
            }

            _pane.Show();
        }

        public void ShowCompare()
        {
            var m = _host.RequireActive();
            if (m == null) return;
            if (_compare != null && _compareMonitor == m)
            {
                _compare.Activate();
                return;
            }

            _compare?.Close();
            _compareMonitor = m;
            _compare = new CompareWindow(new CompareViewModel(_host, m));
            WpfHost.SetOwner(_compare);
            _compare.Closed += (s, e) =>
            {
                _compare = null;
                _compareMonitor = null;
            };
            _compare.Show();
        }

        public void ShowSettings()
        {
            var w = new SettingsWindow(new SettingsViewModel(_host));
            WpfHost.SetOwner(w);
            w.ShowDialog();
        }

        public void ExportToExcel()
        {
            var m = _host.RequireActive();
            if (m == null) return;
            m.Session.FlushNow();

            // Export what the user is looking at: the pane's current filter, or everything.
            var useFilter = _vm != null && _vm.HasFilters;
            IList<ChangeRecord> records = useFilter ? _vm.FilteredRecords.ToList() : m.Session.Records.ToList();
            var description = useFilter ? _vm.FilterDescription : null;

            var dlg = new SaveFileDialog
            {
                Title = "Export change log",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = Names.FileSafe(m.Name) + "_ChangeLog_" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".xlsx",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return;

            var path = dlg.FileName;
            var project = m.Session.Project;
            var integrity = m.Session.Integrity;
            Notify("Exporting " + records.Count.ToString("N0") + " changes…", NotificationKind.Info);
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher; // SWE's UI thread
            Task.Run(() =>
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    ChangeLogExporter.Export(fs, project, records, integrity, description, DateTime.Now);
            }).ContinueWith(t => dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.Exception != null)
                {
                    Log.Error("Export failed", t.Exception);
                    Notify("Export failed: " + t.Exception.GetBaseException().Message, NotificationKind.Error);
                }
                else
                {
                    Notify("Exported " + records.Count.ToString("N0") + " changes to " + Path.GetFileName(path), NotificationKind.Success);
                }
            })), TaskScheduler.Default);
        }

        public void Notify(string message, NotificationKind kind)
        {
            Log.Info("[notify:" + kind + "] " + message);
            if (_vm != null && _pane != null && _pane.IsAlive) _vm.Notify(message, kind);
            else Toast.Show(message, kind);
        }

        public void Shutdown()
        {
            try
            {
                _compare?.Close();
                _pane?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("UI shutdown: " + ex.Message);
            }

            _pane = null;
            _compare = null;
        }
    }
}
