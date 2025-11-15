using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MCLauncher.WPFDataTypes;

namespace MCLauncher
{
    public class MainWindowViewModel : INotifyPropertyChanged, ICommonVersionCommands
    {
        private readonly VersionManager _versionManager;
        private readonly Action _refreshVersionListAction;

        private string _loadingProgressText = "Nothing";
        private double _loadingProgressValue;
        private bool _isLoading;

        public Preferences UserPrefs { get; }

        public VersionList Versions { get; }

        public ICommand CleanupForMicrosoftStoreReinstallCommand { get; }
        public ICommand SignInCommand { get; }
        public ICommand RefreshVersionListCommand { get; }

        public ICommand LaunchCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand RemoveCommand { get; }

        public ICommand OpenLogFileCommand { get; }
        public ICommand OpenDataDirCommand { get; }
        public ICommand OpenGdkCreatorStableCommand { get; }
        public ICommand OpenGdkCreatorPreviewCommand { get; }
        public ICommand ImportPackageCommand { get; }

        public MainWindowViewModel(Preferences userPrefs, VersionList versions, VersionManager versionManager, Action refreshVersionListAction)
        {
            UserPrefs = userPrefs ?? throw new ArgumentNullException(nameof(userPrefs));
            Versions = versions ?? throw new ArgumentNullException(nameof(versions));
            _versionManager = versionManager ?? throw new ArgumentNullException(nameof(versionManager));
            _refreshVersionListAction = refreshVersionListAction ?? throw new ArgumentNullException(nameof(refreshVersionListAction));

            CleanupForMicrosoftStoreReinstallCommand = new RelayCommand(_ => CleanupAllVersions());
            SignInCommand = new RelayCommand(_ => _versionManager.SignIn());
            RefreshVersionListCommand = new RelayCommand(_ => _refreshVersionListAction());

            LaunchCommand = new RelayCommand(v => _versionManager.InvokeLaunch((Version)v));
            DownloadCommand = new RelayCommand(v => _versionManager.InvokeDownload((Version)v));
            RemoveCommand = new RelayCommand(v => _versionManager.InvokeRemove((Version)v));

            OpenLogFileCommand = new RelayCommand(_ => OpenLogFile());
            OpenDataDirCommand = new RelayCommand(_ => OpenDataDir());
            OpenGdkCreatorStableCommand = new RelayCommand(_ => OpenGdkCreatorFolder(false));
            OpenGdkCreatorPreviewCommand = new RelayCommand(_ => OpenGdkCreatorFolder(true));
            ImportPackageCommand = new RelayCommand(async _ => await ImportPackageAsync());
        }

        public string LoadingProgressText
        {
            get => _loadingProgressText;
            set
            {
                if (_loadingProgressText == value)
                    return;
                _loadingProgressText = value;
                OnPropertyChanged(nameof(LoadingProgressText));
            }
        }

        public double LoadingProgressValue
        {
            get => _loadingProgressValue;
            set
            {
                if (Math.Abs(_loadingProgressValue - value) < double.Epsilon)
                    return;
                _loadingProgressValue = value;
                OnPropertyChanged(nameof(LoadingProgressValue));
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading == value)
                    return;
                _isLoading = value;
                OnPropertyChanged(nameof(IsLoading));
            }
        }

        public bool ShowInstalledOnly
        {
            get => UserPrefs.ShowInstalledOnly;
            set
            {
                if (UserPrefs.ShowInstalledOnly == value)
                    return;
                UserPrefs.ShowInstalledOnly = value;
                OnPropertyChanged(nameof(ShowInstalledOnly));
            }
        }

        public bool DeleteAppxAfterDownload
        {
            get => UserPrefs.DeleteAppxAfterDownload;
            set
            {
                if (UserPrefs.DeleteAppxAfterDownload == value)
                    return;
                UserPrefs.DeleteAppxAfterDownload = value;
                OnPropertyChanged(nameof(DeleteAppxAfterDownload));
            }
        }

        public string VersionsApi
        {
            get => UserPrefs.VersionsApi;
            set
            {
                if (UserPrefs.VersionsApi == value)
                    return;
                UserPrefs.VersionsApi = value ?? string.Empty;
                OnPropertyChanged(nameof(VersionsApi));
            }
        }

        private async Task ImportPackageAsync()
        {
            var openFileDlg = new OpenFileDialog
            {
                Filter = "Packages (*.appx;*.msixvc)|*.appx;*.msixvc|All Files|*.*"
            };

            bool? result = openFileDlg.ShowDialog();
            if (result == true)
            {
                await _versionManager.ImportPackageAsync(openFileDlg.FileName, openFileDlg.SafeFileName);
            }
        }

        private void OpenLogFile()
        {
            const string logPath = "Log.txt";
            if (!File.Exists(logPath))
            {
                MessageBox.Show("Log file not found", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Process.Start(logPath);
        }

        private void OpenDataDir()
        {
            Process.Start("explorer.exe", Directory.GetCurrentDirectory());
        }

        private void OpenGdkCreatorFolder(bool preview)
        {
            try
            {
                string path = GdkInstaller.GetCreatorFolder(preview);
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start("explorer.exe", path);
            }
            catch (Exception ex)
            {
                string label = preview ? "Preview" : "Stable";
                MessageBox.Show($"Failed to open GDK Creator folder ({label}):\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CleanupAllVersions()
        {
            var result = MessageBox.Show(
                "Versions of Minecraft installed by the launcher will be uninstalled.\n" +
                "This will allow you to reinstall Minecraft from Microsoft Store. Your data (worlds, etc.) won't be removed.\n\n" +
                "Are you sure you want to continue?",
                "Uninstall all versions",
                MessageBoxButton.OKCancel
            );
            if (result != MessageBoxResult.OK)
                return;

            Debug.WriteLine("Starting uninstall of ALL versions!");
            foreach (var version in Versions)
            {
                if (version.IsInstalled)
                {
                    _versionManager.InvokeRemove(version);
                }
            }
            Debug.WriteLine("Scheduled uninstall of ALL versions.");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
