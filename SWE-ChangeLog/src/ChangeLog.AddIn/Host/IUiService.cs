namespace ChangeLog.AddIn.Host
{
    public enum NotificationKind
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// Everything the host needs from the UI layer. Keeps COM/session code free of WPF so it
    /// can be compiled and reasoned about separately (and swapped for a test double).
    /// </summary>
    public interface IUiService
    {
        void Initialize(AddInHost host);

        void ShowChangeLog();
        void ShowCompare();
        void ShowSettings();
        void ExportToExcel();
        void Notify(string message, NotificationKind kind);
        void Shutdown();
    }
}
