using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MCLauncher {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Threading;
    using Windows.Management.Deployment;
    using Windows.System;
    using WPFDataTypes;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, ICommonVersionCommands {

        private static readonly string MINECRAFT_PACKAGE_FAMILY = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";

        private VersionList _versions;
        private readonly VersionDownloader _anonVersionDownloader = new VersionDownloader();
        private readonly VersionDownloader _userVersionDownloader = new VersionDownloader();
        private readonly Task _userVersionDownloaderLoginTask;
        private int _userVersionDownloaderLoginTaskStarted;

        public MainWindow() {
            InitializeComponent();
            _versions = new VersionList(this);
            VersionList.ItemsSource = _versions;
            _userVersionDownloaderLoginTask = new Task(() => {
                _userVersionDownloader.EnableUserAuthorization();
            });
            Dispatcher.Invoke(async () => {
                try {
                    await _versions.DownloadList();
                } catch (Exception e) {
                    Debug.WriteLine("List download failed:\n" + e.ToString());
                }
            });
        }

        public ICommand LaunchCommand => new RelayCommand((v) => InvokeLaunch((Version) v));

        public ICommand DownloadCommand => new RelayCommand((v) => InvokeDownload((Version)v));

        private void InvokeLaunch(Version v) {
            Task.Run(async () => {
                string gameDir = Path.GetFullPath(v.GameDirectory);
                try {
                    await ReRegisterPackage(gameDir);
                } catch (Exception e) {
                    Debug.WriteLine("App re-register failed:\n" + e.ToString());
                    MessageBox.Show("App re-register failed:\n" + e.ToString());
                    return;
                }

                try {
                    var pkg = await AppDiagnosticInfo.RequestInfoForPackageAsync(MINECRAFT_PACKAGE_FAMILY);
                    if (pkg.Count > 0)
                        await pkg[0].LaunchAsync();
                } catch (Exception e) {
                    Debug.WriteLine("App launch failed:\n" + e.ToString());
                    MessageBox.Show("App launch failed:\n" + e.ToString());
                    return;
                }
            });
        }

        private async Task ReRegisterPackage(string gameDir) {
            PackageManager packageManager = new PackageManager();
            foreach (var pkg in packageManager.FindPackages(MINECRAFT_PACKAGE_FAMILY)) {
                if (pkg.InstalledLocation.Path == gameDir) {
                    Debug.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + pkg.InstalledLocation.Path);
                    return;
                }
                Debug.WriteLine("Removing package: " + pkg.Id.FullName + " " + pkg.InstalledLocation.Path);
                if (!pkg.IsDevelopmentMode) {
                    if (MessageBox.Show("A non-Development Mode version is installed on this system. It will need to be removed in a way which does not preserve the data (including your saved worlds). Are you sure you want to continue?", "Warning", MessageBoxButton.YesNoCancel) != MessageBoxResult.Yes)
                        return;
                    await packageManager.RemovePackageAsync(pkg.Id.FullName, 0);
                } else {
                    await packageManager.RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData);
                }
                Debug.WriteLine("Removal of package done: " + pkg.Id.FullName);
            }
            Debug.WriteLine("Registering package");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");
            await packageManager.RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode);
            Debug.WriteLine("App re-register done!");
        }

        private void InvokeDownload(Version v) {
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            v.DownloadInfo = new VersionDownloadInfo();
            v.DownloadInfo.IsInitializing = true;
            v.DownloadInfo.CancelCommand = new RelayCommand((o) => cancelSource.Cancel());

            Debug.WriteLine("Download start");
            Task.Run(async () => {
                string dlPath = "Minecraft-" + v.Name + ".Appx";
                VersionDownloader downloader = _anonVersionDownloader;
                if (v.IsBeta) {
                    downloader = _userVersionDownloader;
                    if (Interlocked.CompareExchange(ref _userVersionDownloaderLoginTaskStarted, 1, 0) == 0) {
                        _userVersionDownloaderLoginTask.Start();
                    }
                    await _userVersionDownloaderLoginTask;
                }
                try {
                    await downloader.Download(v.UUID, "1", dlPath, (current, total) => {
                        if (v.DownloadInfo.IsInitializing) {
                            Debug.WriteLine("Actual download started");
                            v.DownloadInfo.IsInitializing = false;
                            if (total.HasValue)
                                v.DownloadInfo.TotalSize = total.Value;
                        }
                        v.DownloadInfo.DownloadedBytes = current;
                    }, cancelSource.Token);
                    Debug.WriteLine("Download complete");
                } catch (Exception e) {
                    Debug.WriteLine("Download failed:\n" + e.ToString());
                    if (!(e is TaskCanceledException))
                        MessageBox.Show("Download failed:\n" + e.ToString());
                    v.DownloadInfo = null;
                    return;
                }
                try {
                    v.DownloadInfo.IsExtracting = true;
                    string dirPath = v.GameDirectory;
                    if (Directory.Exists(dirPath))
                        Directory.Delete(dirPath, true);
                    ZipFile.ExtractToDirectory(dlPath, dirPath);
                    v.DownloadInfo = null;
                    File.Delete(Path.Combine(dirPath, "AppxSignature.p7x"));
                } catch (Exception e) {
                    Debug.WriteLine("Extraction failed:\n" + e.ToString());
                    MessageBox.Show("Extraction failed:\n" + e.ToString());
                    v.DownloadInfo = null;
                    return;
                }
                v.DownloadInfo = null;
                v.UpdateInstallStatus();
            });
        }
    }

    namespace WPFDataTypes {

        public class NotifyPropertyChangedBase : INotifyPropertyChanged {

            public event PropertyChangedEventHandler PropertyChanged;

            protected void OnPropertyChanged(string name) {
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs(name));
            }

        }

        public interface ICommonVersionCommands {

            ICommand LaunchCommand { get; }

            ICommand DownloadCommand { get; }

        }

        public class Versions : List<Object> {
        }

        public class Version : NotifyPropertyChangedBase {

            public Version() { }
            public Version(string uuid, string name, bool isBeta, ICommonVersionCommands commands) {
                this.UUID = uuid;
                this.Name = name;
                this.IsBeta = isBeta;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
            }

            public string UUID { get; set; }
            public string Name { get; set; }
            public bool IsBeta { get; set; }

            public string GameDirectory => "Minecraft-" + Name;

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

            private VersionDownloadInfo _downloadInfo;
            public VersionDownloadInfo DownloadInfo {
                get { return _downloadInfo; }
                set { _downloadInfo = value; OnPropertyChanged("DownloadInfo"); OnPropertyChanged("IsDownloading"); }
            }

            public bool IsDownloading => DownloadInfo != null;

            public void UpdateInstallStatus() {
                OnPropertyChanged("IsInstalled");
            }

        }

        public class VersionDownloadInfo : NotifyPropertyChangedBase {

            private bool _isInitializing;
            private bool _isExtracting;
            private long _downloadedBytes;
            private long _totalSize;

            public bool IsInitializing {
                get { return _isInitializing; }
                set { _isInitializing = value; OnPropertyChanged("IsProgressIndeterminate"); OnPropertyChanged("DisplayStatus"); }
            }

            public bool IsExtracting {
                get { return _isExtracting; }
                set { _isExtracting = value; OnPropertyChanged("IsProgressIndeterminate"); OnPropertyChanged("DisplayStatus"); }
            }

            public bool IsProgressIndeterminate {
                get { return IsInitializing || IsExtracting; }
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
                    if (IsInitializing)
                        return "Downloading...";
                    if (IsExtracting)
                        return "Extracting...";
                    return "Downloading... " + (DownloadedBytes / 1024 / 1024) + "MiB/" + (TotalSize / 1024 / 1024) + "MiB";
                }
            }

            public ICommand CancelCommand { get; set; }

        }

    }
}
