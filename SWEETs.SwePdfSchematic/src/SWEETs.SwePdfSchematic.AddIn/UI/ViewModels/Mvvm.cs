using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SWEETs.SwePdfSchematic.AddIn.UI.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _run;
        private readonly Func<object, bool> _can;
        public RelayCommand(Action<object> run, Func<object, bool> can = null) { _run = run; _can = can; }
        public RelayCommand(Action run, Func<bool> can = null) : this(_ => run(), can == null ? (Func<object, bool>)null : _ => can()) { }
        public event EventHandler CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public bool CanExecute(object p) => _can?.Invoke(p) ?? true;
        public void Execute(object p) => _run(p);
    }

    /// <summary>Async command that disables itself while running and reports failures through a callback.</summary>
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _run;
        private readonly Func<bool> _can;
        private readonly Action<Exception> _onError;
        private bool _busy;
        public AsyncCommand(Func<Task> run, Func<bool> can, Action<Exception> onError) { _run = run; _can = can; _onError = onError; }
        public event EventHandler CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public bool CanExecute(object p) => !_busy && (_can?.Invoke() ?? true);
        public async void Execute(object p)
        {
            _busy = true; CommandManager.InvalidateRequerySuggested();
            try { await _run(); }
            catch (Exception ex) { _onError?.Invoke(ex); }
            finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
        }
    }
}
