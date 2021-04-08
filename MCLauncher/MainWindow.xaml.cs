using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MCLauncher {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Threading;
    using System.Windows.Data;
    using Windows.ApplicationModel;
    using Windows.Foundation;
    using Windows.Management.Core;
    using Windows.Management.Deployment;
    using Windows.System;
    using WPFDataTypes;

    using System;
    using System.Windows;
    using System.Windows.Controls;
    using REghZyFramework.Themes;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, ICommonVersionCommands {

        private static readonly string MINECRAFT_PACKAGE_FAMILY = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        private static readonly string PREFS_PATH = @"preferences.json";
        private static readonly string IMPORTED_VERSIONS_PATH = @"imported_versions";

        private VersionList _versions;
        public Preferences UserPrefs { get; }
        private readonly VersionDownloader _anonVersionDownloader = new VersionDownloader();
        private readonly VersionDownloader _userVersionDownloader = new VersionDownloader();
        private readonly Task _userVersionDownloaderLoginTask;
        private volatile int _userVersionDownloaderLoginTaskStarted;
        private volatile bool _hasLaunchTask = false;

        public MainWindow() {
            InitializeComponent();
            ShowBetasCheckbox.DataContext = this;
            ShowInstalledVersionsOnlyCheckbox.DataContext = this;

            if (File.Exists(PREFS_PATH)) {
                UserPrefs = JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(PREFS_PATH));
            } else {
                UserPrefs = new Preferences();
                RewritePrefs();
            }

            _versions = new VersionList("versions.json", IMPORTED_VERSIONS_PATH, this);
            VersionList.ItemsSource = _versions;
            var view = CollectionViewSource.GetDefaultView(VersionList.ItemsSource) as CollectionView;
            view.Filter = VersionListFilter;
            _userVersionDownloaderLoginTask = new Task(() => {
                _userVersionDownloader.EnableUserAuthorization();
            });
            Dispatcher.Invoke(async () => {
                try {
                    await _versions.LoadFromCache();
                } catch (Exception e) {
                    Debug.WriteLine("List cache load failed:\n" + e.ToString());
                }
                try {
                    await _versions.DownloadList();
                } catch (Exception e) {
                    Debug.WriteLine("List download failed:\n" + e.ToString());
                }
                await _versions.LoadImported();
            });
            ThemesController.SetTheme(GetThemeEnum(UserPrefs.Theme));
        }

        private ThemesController.ThemeTypes GetThemeEnum(string name) {
            switch (name.ToLower()) {
                case "colourfuldarktheme":
                case "colorfuldarktheme":
                    return ThemesController.ThemeTypes.ColourfulDark;
                case "colourfullighttheme":
                case "colorfullighttheme":
                    return ThemesController.ThemeTypes.ColourfulLight;
                case "darktheme":
                    return ThemesController.ThemeTypes.Dark;
                default:
                    return ThemesController.ThemeTypes.Light; // Light is default :/
            }
        }

        private async void ImportButtonClicked(object sender, RoutedEventArgs e) {
            Microsoft.Win32.OpenFileDialog openFileDlg = new Microsoft.Win32.OpenFileDialog();
            Nullable<bool> result = openFileDlg.ShowDialog();
            if (result == true) {
                string directory = Path.Combine(IMPORTED_VERSIONS_PATH, openFileDlg.SafeFileName);
                if (Directory.Exists(directory)) {
                    MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("A version with the same name was already imported. Do you want to delete it ?", "Delete Confirmation", System.Windows.MessageBoxButton.YesNo);
                    if (messageBoxResult == MessageBoxResult.Yes) {
                        Directory.Delete(directory, true);
                    } else {
                        return;
                    }
                }

                var versionEntry = _versions.AddEntry(openFileDlg.SafeFileName, directory);
                await Task.Run(() => {
                    versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Extracting);
                    ZipFile.ExtractToDirectory(openFileDlg.FileName, directory);
                    versionEntry.StateChangeInfo = null;
                });
                MessageBox.Show("Successfully imported appx: " + openFileDlg.FileName);
            }
        }

        public ICommand LaunchCommand => new RelayCommand((v) => InvokeLaunch((WPFDataTypes.Version)v));

        public ICommand RemoveCommand => new RelayCommand((v) => InvokeRemove((WPFDataTypes.Version)v));

        public ICommand DownloadCommand => new RelayCommand((v) => InvokeDownload((WPFDataTypes.Version)v));

        private void InvokeLaunch(WPFDataTypes.Version v) {
            if (_hasLaunchTask)
                return;
            _hasLaunchTask = true;
            Task.Run(async () => {
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Launching);
                string gameDir = Path.GetFullPath(v.GameDirectory);
                try {
                    await ReRegisterPackage(gameDir);
                } catch (Exception e) {
                    Debug.WriteLine("App re-register failed:\n" + e.ToString());
                    MessageBox.Show("App re-register failed:\n" + e.ToString());
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                    return;
                }

                try {
                    var pkg = await AppDiagnosticInfo.RequestInfoForPackageAsync(MINECRAFT_PACKAGE_FAMILY);
                    if (pkg.Count > 0)
                        await pkg[0].LaunchAsync();
                    Debug.WriteLine("App launch finished!");
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                } catch (Exception e) {
                    Debug.WriteLine("App launch failed:\n" + e.ToString());
                    MessageBox.Show("App launch failed:\n" + e.ToString());
                    _hasLaunchTask = false;
                    v.StateChangeInfo = null;
                    return;
                }
            });
        }

        private async Task DeploymentProgressWrapper(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> t) {
            TaskCompletionSource<int> src = new TaskCompletionSource<int>();
            t.Progress += (v, p) => {
                Debug.WriteLine("Deployment progress: " + p.state + " " + p.percentage + "%");
            };
            t.Completed += (v, p) => {
                if (p == AsyncStatus.Error) {
                    Debug.WriteLine("Deployment failed: " + v.GetResults().ErrorText);
                    src.SetException(new Exception("Deployment failed: " + v.GetResults().ErrorText));
                } else {
                    Debug.WriteLine("Deployment done: " + p);
                    src.SetResult(1);
                }
            };
            await src.Task;
        }

        private string GetBackupMinecraftDataDir() {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tmpDir = Path.Combine(localAppData, "TmpMinecraftLocalState");
            return tmpDir;
        }

        private void BackupMinecraftDataForRemoval() {
            var data = ApplicationDataManager.CreateForPackageFamily(MINECRAFT_PACKAGE_FAMILY);
            string tmpDir = GetBackupMinecraftDataDir();
            if (Directory.Exists(tmpDir)) {
                Debug.WriteLine("BackupMinecraftDataForRemoval error: " + tmpDir + " already exists");
                Process.Start("explorer.exe", tmpDir);
                MessageBox.Show("The temporary directory for backing up MC data already exists. This probably means that we failed last time backing up the data. Please back the directory up manually.");
                throw new Exception("Temporary dir exists");
            }
            Debug.WriteLine("Moving Minecraft data to: " + tmpDir);
            Directory.Move(data.LocalFolder.Path, tmpDir);
        }

        private void RestoreMove(string from, string to) {
            foreach (var f in Directory.EnumerateFiles(from)) {
                string ft = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(ft)) {
                    if (MessageBox.Show("The file " + ft + " already exists in the destination.\nDo you want to replace it? The old file will be lost otherwise.", "Restoring data directory from previous installation", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        continue;
                    File.Delete(ft);
                }
                File.Move(f, ft);
            }
            foreach (var f in Directory.EnumerateDirectories(from)) {
                string tp = Path.Combine(to, Path.GetFileName(f));
                if (!Directory.Exists(tp)) {
                    if (File.Exists(tp) && MessageBox.Show("The file " + tp + " is not a directory. Do you want to remove it? The data from the old directory will be lost otherwise.", "Restoring data directory from previous installation", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        continue;
                    Directory.CreateDirectory(tp);
                }
                RestoreMove(f, tp);
            }
        }

        private void RestoreMinecraftDataFromReinstall() {
            string tmpDir = GetBackupMinecraftDataDir();
            if (!Directory.Exists(tmpDir))
                return;
            var data = ApplicationDataManager.CreateForPackageFamily(MINECRAFT_PACKAGE_FAMILY);
            Debug.WriteLine("Moving backup Minecraft data to: " + data.LocalFolder.Path);
            RestoreMove(tmpDir, data.LocalFolder.Path);
            Directory.Delete(tmpDir, true);
        }

        private async Task RemovePackage(Package pkg) {
            Debug.WriteLine("Removing package: " + pkg.Id.FullName);
            if (!pkg.IsDevelopmentMode) {
                BackupMinecraftDataForRemoval();
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, 0));
            } else {
                Debug.WriteLine("Package is in development mode");
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData));
            }
            Debug.WriteLine("Removal of package done: " + pkg.Id.FullName);
        }

        private string GetPackagePath(Package pkg) {
            try {
                return pkg.InstalledLocation.Path;
            } catch (FileNotFoundException) {
                return "";
            }
        }

        private async Task UnregisterPackage(string gameDir) {
            foreach (var pkg in new PackageManager().FindPackages(MINECRAFT_PACKAGE_FAMILY)) {
                string location = GetPackagePath(pkg);
                if (location == "" || location == gameDir) {
                    await RemovePackage(pkg);
                }
            }
        }

        private async Task ReRegisterPackage(string gameDir) {
            foreach (var pkg in new PackageManager().FindPackages(MINECRAFT_PACKAGE_FAMILY)) {
                string location = GetPackagePath(pkg);
                if (location == gameDir) {
                    Debug.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + location);
                    return;
                }
                await RemovePackage(pkg);
            }
            Debug.WriteLine("Registering package");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");
            await DeploymentProgressWrapper(new PackageManager().RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode));
            Debug.WriteLine("App re-register done!");
            RestoreMinecraftDataFromReinstall();
        }

        private void InvokeDownload(WPFDataTypes.Version v) {
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Initializing);
            v.StateChangeInfo.CancelCommand = new RelayCommand((o) => cancelSource.Cancel());

            Debug.WriteLine("Download start");
            Task.Run(async () => {
                string dlPath = "Minecraft-" + v.Name + ".Appx";
                VersionDownloader downloader = _anonVersionDownloader;
                if (v.IsBeta) {
                    downloader = _userVersionDownloader;
                    if (Interlocked.CompareExchange(ref _userVersionDownloaderLoginTaskStarted, 1, 0) == 0) {
                        _userVersionDownloaderLoginTask.Start();
                    }
                    Debug.WriteLine("Waiting for authentication");
                    try {
                        await _userVersionDownloaderLoginTask;
                        Debug.WriteLine("Authentication complete");
                    } catch (Exception e) {
                        v.StateChangeInfo = null;
                        Debug.WriteLine("Authentication failed:\n" + e.ToString());
                        MessageBox.Show("Failed to authenticate. Please make sure your account is subscribed to the beta programme.\n\n" + e.ToString(), "Authentication failed");
                        return;
                    }
                }
                try {
                    await downloader.Download(v.UUID, "1", dlPath, (current, total) => {
                        if (v.StateChangeInfo.VersionState != VersionState.Downloading) {
                            Debug.WriteLine("Actual download started");
                            v.StateChangeInfo.VersionState = VersionState.Downloading;
                            if (total.HasValue)
                                v.StateChangeInfo.TotalSize = total.Value;
                        }
                        v.StateChangeInfo.DownloadedBytes = current;
                    }, cancelSource.Token);
                    Debug.WriteLine("Download complete");
                } catch (Exception e) {
                    Debug.WriteLine("Download failed:\n" + e.ToString());
                    if (!(e is TaskCanceledException))
                        MessageBox.Show("Download failed:\n" + e.ToString());
                    v.StateChangeInfo = null;
                    return;
                }
                try {
                    v.StateChangeInfo.VersionState = VersionState.Extracting;
                    string dirPath = v.GameDirectory;
                    if (Directory.Exists(dirPath))
                        Directory.Delete(dirPath, true);
                    ZipFile.ExtractToDirectory(dlPath, dirPath);
                    v.StateChangeInfo = null;
                    File.Delete(Path.Combine(dirPath, "AppxSignature.p7x"));
                    File.Delete(dlPath);
                } catch (Exception e) {
                    Debug.WriteLine("Extraction failed:\n" + e.ToString());
                    MessageBox.Show("Extraction failed:\n" + e.ToString());
                    v.StateChangeInfo = null;
                    return;
                }
                v.StateChangeInfo = null;
                v.UpdateInstallStatus();
            });
        }

        private void InvokeRemove(WPFDataTypes.Version v) {
            Task.Run(async () => {
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Uninstalling);
                await UnregisterPackage(Path.GetFullPath(v.GameDirectory));
                Directory.Delete(v.GameDirectory, true);
                v.StateChangeInfo = null;
                if (v.UUID == WPFDataTypes.Version.UNKNOWN_UUID) {
                    Dispatcher.Invoke(() => _versions.Remove(v));
                    Debug.WriteLine("Removed imported version " + v.DisplayName);
                } else {
                    v.UpdateInstallStatus();
                    Debug.WriteLine("Removed release version " + v.DisplayName);
                }
            });
        }

        private void ShowBetaVersionsCheck_Changed(object sender, RoutedEventArgs e) {
            UserPrefs.ShowBetas = ShowBetasCheckbox.IsChecked ?? false;
            CollectionViewSource.GetDefaultView(VersionList.ItemsSource).Refresh();
            RewritePrefs();
        }

        private void ShowInstalledOnlyCheck_Changed(object sender, RoutedEventArgs e) {
            UserPrefs.ShowInstalledOnly = ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false;
            CollectionViewSource.GetDefaultView(VersionList.ItemsSource).Refresh();
            RewritePrefs();
        }

        private bool VersionListFilter(object obj) {
            var v = obj as WPFDataTypes.Version;
            return (!v.IsBeta || UserPrefs.ShowBetas) && (v.IsInstalled || !UserPrefs.ShowInstalledOnly);
        }

        private void RewritePrefs() {
            File.WriteAllText(PREFS_PATH, JsonConvert.SerializeObject(UserPrefs));
        }

        private void ChangeTheme(object sender, RoutedEventArgs e) {
            ThemesController.ThemeTypes? theme = null;
            switch (int.Parse(((MenuItem)sender).Uid)) {
                case 0:
                    theme = ThemesController.ThemeTypes.Light;
                    break;
                case 1: 
                    theme = ThemesController.ThemeTypes.ColourfulLight;
                    break;
                case 2:
                    theme = ThemesController.ThemeTypes.Dark;
                    break;
                case 3: 
                    theme = ThemesController.ThemeTypes.ColourfulDark; 
                    break;
            }
            if (theme == null) return;
            ThemesController.SetTheme((ThemesController.ThemeTypes) theme);
            UserPrefs.Theme = ThemesController.GetThemeName((ThemesController.ThemeTypes) theme);
            RewritePrefs();
            e.Handled = true;
        }
    }

    namespace WPFDataTypes {

        public class NotifyPropertyChangedBase : INotifyPropertyChanged {

            public event PropertyChangedEventHandler PropertyChanged;

            protected void OnPropertyChanged(string name) {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }

        }

        public interface ICommonVersionCommands {

            ICommand LaunchCommand { get; }

            ICommand DownloadCommand { get; }

            ICommand RemoveCommand { get; }

        }

        public class Version : NotifyPropertyChangedBase {
            public static readonly string UNKNOWN_UUID = "UNKNOWN";

            public Version(string uuid, string name, bool isBeta, ICommonVersionCommands commands) {
                this.UUID = uuid;
                this.Name = name;
                this.IsBeta = isBeta;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
                this.RemoveCommand = commands.RemoveCommand;
                this.GameDirectory = "Minecraft-" + Name;
            }
            public Version(string uuid, string name, bool isBeta, string directory, ICommonVersionCommands commands) {
                this.UUID = uuid;
                this.Name = name;
                this.IsBeta = isBeta;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
                this.RemoveCommand = commands.RemoveCommand;
                this.GameDirectory = directory;
            }

            public string UUID { get; set; }
            public string Name { get; set; }
            public bool IsBeta { get; set; }

            public string GameDirectory { get; set; }

            public bool IsInstalled => Directory.Exists(GameDirectory);

            public string DisplayName {
                get {
                    return Name + (IsBeta ? " (beta)" : "");
                }
            }
            public string DisplayInstallStatus {
                get {
                    return IsInstalled ? "Installed" : "Not installed";
                }
            }

            public ICommand LaunchCommand { get; set; }
            public ICommand DownloadCommand { get; set; }
            public ICommand RemoveCommand { get; set; }

            private VersionStateChangeInfo _stateChangeInfo;
            public VersionStateChangeInfo StateChangeInfo {
                get { return _stateChangeInfo; }
                set { _stateChangeInfo = value; OnPropertyChanged("StateChangeInfo"); OnPropertyChanged("IsStateChanging"); }
            }

            public bool IsStateChanging => StateChangeInfo != null;

            public void UpdateInstallStatus() {
                OnPropertyChanged("IsInstalled");
            }

        }

        public enum VersionState {
            Initializing,
            Downloading,
            Extracting,
            Launching,
            Uninstalling
        };

        public class VersionStateChangeInfo : NotifyPropertyChangedBase {

            private VersionState _versionState;

            private long _downloadedBytes;
            private long _totalSize;

            public VersionStateChangeInfo(VersionState versionState) {
                _versionState = versionState;
            }

            public VersionState VersionState {
                get { return _versionState; }
                set {
                    _versionState = value;
                    OnPropertyChanged("IsProgressIndeterminate");
                    OnPropertyChanged("DisplayStatus");
                }
            }

            public bool IsProgressIndeterminate {
                get {
                    switch (_versionState) {
                        case VersionState.Initializing:
                        case VersionState.Extracting:
                        case VersionState.Uninstalling:
                        case VersionState.Launching:
                            return true;
                        default: return false;
                    }
                }
            }

            public long DownloadedBytes {
                get { return _downloadedBytes; }
                set { _downloadedBytes = value; OnPropertyChanged("DownloadedBytes"); OnPropertyChanged("DisplayStatus"); }
            }

            public long TotalSize {
                get { return _totalSize; }
                set { _totalSize = value; OnPropertyChanged("TotalSize"); OnPropertyChanged("DisplayStatus"); }
            }

            public string DisplayStatus {
                get {
                    switch(_versionState)
                    {
                        case VersionState.Initializing: return "Preparing...";
                        case VersionState.Downloading:
                            return "Downloading... " + (DownloadedBytes / 1024 / 1024) + "MiB/" + (TotalSize / 1024 / 1024) + "MiB";
                        case VersionState.Extracting: return "Extracting...";
                        case VersionState.Launching: return "Launching...";
                        case VersionState.Uninstalling: return "Uninstalling...";
                        default: return "Wtf is happening? ...";
                    }
                }
            }

            public ICommand CancelCommand { get; set; }

        }

    }
}
