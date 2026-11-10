using System;
using System.Windows.Input;

namespace VendorGrid.UI
{
    public sealed class RelayCommand : ICommand
    {
        readonly Action<object> _execute;
        readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute; _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(parameter); }
        public void Execute(object parameter) { _execute(parameter); }
        public void Raise() { var h = CanExecuteChanged; if (h != null) h(this, EventArgs.Empty); }
    }
}
