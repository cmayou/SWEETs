using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using ChangeLog.AddIn.Host;

namespace ChangeLog.AddIn.UI
{
    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly AddInHost _host;

        public SettingsViewModel(AddInHost host)
        {
            _host = host;
            Settings = host.Settings.Clone();
            OpenLogCommand = new RelayCommand(() =>
            {
                Directory.CreateDirectory(AddInSettings.LocalFolder);
                Process.Start("explorer.exe", "\"" + AddInSettings.LocalFolder + "\"");
            });
            OpenStoreCommand = new RelayCommand(() => Process.Start("explorer.exe", "\"" + StoreLocation + "\""), () => Directory.Exists(StoreLocation ?? string.Empty));
        }

        public AddInSettings Settings { get; }

        public string SigningKeyId => _host.Signer?.KeyId ?? "—";
        public string Identity => _host.Who == null ? "—" : _host.Who.DisplayName + "  (" + _host.Who.WindowsUser + " @ " + _host.Who.Machine + ")";
        public string StoreLocation => _host.Active?.Session.Project.StoreRoot;
        public string Version => "Change Log " + AddInHost.AddInVersion + " · SWE " + (_host.AppVersion ?? "?");

        public ICommand OpenLogCommand { get; }
        public ICommand OpenStoreCommand { get; }

        public void Save() => _host.ApplySettings(Settings);
    }
}
