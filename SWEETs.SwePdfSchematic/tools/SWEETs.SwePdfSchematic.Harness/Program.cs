using System;
using System.Linq;
using System.Windows;
using SWEETs.SwePdfSchematic.AddIn.UI;

namespace SWEETs.SwePdfSchematic.Harness
{
    /// <summary>
    /// Opens the importer without SOLIDWORKS Electrical: analysis, review, mapping and "Simulate" all work;
    /// "Build project" stays disabled. Optional argument: a PDF path to load immediately.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(null);
            var pdf = args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf != null)
                window.Loaded += async (s, e) => await ((AddIn.UI.ViewModels.MainViewModel)window.DataContext).LoadAsync(pdf);
            app.Run(window);
        }
    }
}
