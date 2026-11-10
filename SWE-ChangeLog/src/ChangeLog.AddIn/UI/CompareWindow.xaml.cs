using System;
using System.Windows;

namespace ChangeLog.AddIn.UI
{
    public partial class CompareWindow : Window
    {
        public CompareWindow(CompareViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            Closed += OnClosed;
        }

        private void OnClosed(object sender, EventArgs e) => ((CompareViewModel)DataContext).Detach();
    }
}
