using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Model;
using SWEETs.SweDrawingParameters.AddIn.Services;

namespace SWEETs.SweDrawingParameters.AddIn.UI.ViewModels
{
    /// <summary>Runs a <see cref="RunPlan"/> drawing by drawing and shows progress and results.</summary>
    public sealed class RunViewModel : ObservableObject
    {
        private readonly RunPlan _plan;
        private readonly string _backupFolder;
        private bool _cancel;

        public RunViewModel(RunPlan plan)
        {
            _plan = plan;
            _backupFolder = AppPaths.NewBackupFolder(plan.ProjectName);
            foreach (var d in plan.Drawings) Rows.Add(new DrawingRowViewModel(d) { State = RowState.Queued });

            CancelCommand = new RelayCommand(() => { _cancel = true; ProgressText = "Cancelling after the current drawing…"; }, () => IsRunning);
            OpenBackupsCommand = new RelayCommand(() => MainViewModel.OpenFolder(_backupFolder));
            OpenLogCommand = new RelayCommand(() => MainViewModel.OpenFolder(FileLog.Folder));
        }

        public string ProjectName => _plan.ProjectName;
        public ObservableCollection<DrawingRowViewModel> Rows { get; } = new ObservableCollection<DrawingRowViewModel>();

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) CommandManager.InvalidateRequerySuggested(); } }
        private double _progress;
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        private string _progressText = "Starting…";
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
        private Banner _bannerKind = Banner.Info;
        public Banner BannerKind { get => _bannerKind; private set => Set(ref _bannerKind, value); }
        private string _bannerText = "Updating drawings. Each one is backed up, updated, then checked twice before it replaces the original.";
        public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }

        public int VerifiedCount => Rows.Count(r => r.State == RowState.Verified);
        public int ProblemCount => Rows.Count(r => r.State == RowState.Failed || r.State == RowState.Skipped);

        public ICommand CancelCommand { get; }
        public ICommand OpenBackupsCommand { get; }
        public ICommand OpenLogCommand { get; }

        public async Task RunAsync()
        {
            IsRunning = true;
            var updater = new DrawingUpdater(_backupFolder);
            FileLog.Write("update", $"Run started: {Rows.Count} drawing(s), backups in {_backupFolder}. " +
                                    $"Requested: snap {_plan.Request.SnapText}, grid {_plan.Request.GridText}, zone {_plan.Request.ZoneText}, ortho {DrawingParameters.OnOff(_plan.Request.OrthoOn)}.");
            int index = 0, verified = 0, unchanged = 0, problems = 0;
            try
            {
                foreach (var row in Rows)
                {
                    if (_cancel) break;
                    index++;
                    ProgressText = $"Updating {index} of {Rows.Count}: {row.Name}";
                    Progress = (double)(index - 1) / Rows.Count;
                    row.State = RowState.Working;
                    await Task.Delay(30);   // let the row repaint

                    var drawing = row.Drawing;
                    var request = _plan.Request;
                    var result = DrawingUpdater.CheckAccess(drawing)                      // COM, on SWE's UI thread
                                 ?? await Task.Run(() => updater.Update(drawing, request));  // file work only
                    row.Detail = result.Message;
                    if (result.After != null) row.Current = result.After;
                    switch (result.Outcome)
                    {
                        case UpdateOutcome.Verified: row.State = RowState.Verified; verified++; break;
                        case UpdateOutcome.Unchanged: row.State = RowState.Verified; unchanged++; break;
                        case UpdateOutcome.Skipped: row.State = RowState.Skipped; problems++; break;
                        default: row.State = RowState.Failed; problems++; break;
                    }
                    RaiseCounts();
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("update", ex);
                Show(Banner.Error, "Stopped: " + ex.Message);
            }
            finally
            {
                foreach (var q in Rows.Where(x => x.State == RowState.Queued)) { q.State = RowState.Skipped; q.Detail = "Not processed"; }
                Progress = 1;
                ProgressText = "";
                IsRunning = false;
                RaiseCounts();
            }

            var already = unchanged > 0 ? $" {unchanged} already had these values." : "";
            if (BannerKind == Banner.Error) { }
            else if (_cancel)
                Show(Banner.Warning, $"Cancelled after {index} drawing(s): {verified + unchanged} done, {problems} with problems.");
            else if (problems == 0)
                Show(Banner.Success, $"{verified} drawing(s) updated and verified.{already} Open a drawing to check its Drawing parameters.");
            else
                Show(Banner.Warning, $"{verified} drawing(s) updated and verified, {problems} with problems. Hover over a status to see why.{already}");
            FileLog.Write("update", $"Run finished: {verified} updated, {unchanged} unchanged, {problems} problem(s){(_cancel ? ", cancelled" : "")}.");
        }

        private void Show(Banner kind, string text) { BannerKind = kind; BannerText = text; }

        private void RaiseCounts() { Raise(nameof(VerifiedCount)); Raise(nameof(ProblemCount)); }
    }
}
