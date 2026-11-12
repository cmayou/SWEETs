using System.Threading;
using System.Windows.Threading;

namespace ChangeLog.AddIn.Host
{
    /// <summary>
    /// SWE's UI thread is an MFC thread: it has no SynchronizationContext, so await/TaskScheduler
    /// continuations and ChangeLogSession callbacks would otherwise run on thread-pool threads.
    /// Install WPF's dispatcher context (WPF's Dispatcher is pumped by MFC's message loop).
    /// Re-checked at every entry point because another add-in may have replaced it.
    /// </summary>
    public static class UiThread
    {
        public static void EnsureContext()
        {
            if (!(SynchronizationContext.Current is DispatcherSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        }
    }
}
