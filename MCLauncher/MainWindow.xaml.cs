﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace MCLauncher {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Threading;
    using Windows.Foundation;
    using Windows.Management.Core;
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
        private volatile int _userVersionDownloaderLoginTaskStarted;
        private volatile bool _hasLaunchTask = false;

        public MainWindow() {
            InitializeComponent();
            _versions = new VersionList("versions.json", this);
            VersionList.ItemsSource = _versions;
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
            });
        }

        private async void ImportButtonClicked(object sender, RoutedEventArgs e) {
            Microsoft.Win32.OpenFileDialog openFileDlg = new Microsoft.Win32.OpenFileDialog();
            Nullable<bool> result = openFileDlg.ShowDialog();
            if (result == true) {
                string directory = openFileDlg.SafeFileName;
                    if (Directory.Exists(directory)) {
                    MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("A version with the same name was already imported. Do you want to delete it ?", "Delete Confirmation", System.Windows.MessageBoxButton.YesNo);
                    if (messageBoxResult == MessageBoxResult.Yes) {
                        Directory.Delete(directory, true);
                    } else {
                        return;
                    }
                }
                await Task.Run(() => ZipFile.ExtractToDirectory(openFileDlg.FileName, directory));
                _versions.AddEntry(openFileDlg.SafeFileName);
            }
        }

        public ICommand LaunchCommand => new RelayCommand((v) => InvokeLaunch((Version)v));

        public ICommand RemoveCommand => new RelayCommand((v) => InvokeRemove((Version)v));

        public ICommand DownloadCommand => new RelayCommand((v) => InvokeDownload((Version)v));

        private void InvokeLaunch(Version v) {
            if (_hasLaunchTask)
                return;
            _hasLaunchTask = true;
            Task.Run(async () => {
                string gameDir = Path.GetFullPath(v.GameDirectory);
                try {
                    await ReRegisterPackage(gameDir);
                } catch (Exception e) {
                    Debug.WriteLine("App re-register failed:\n" + e.ToString());
                    MessageBox.Show("App re-register failed:\n" + e.ToString());
                    _hasLaunchTask = false;
                    return;
                }

                try {
                    var pkg = await AppDiagnosticInfo.RequestInfoForPackageAsync(MINECRAFT_PACKAGE_FAMILY);
                    if (pkg.Count > 0)
                        await pkg[0].LaunchAsync();
                    Debug.WriteLine("App launch finished!");
                    _hasLaunchTask = false;
                } catch (Exception e) {
                    Debug.WriteLine("App launch failed:\n" + e.ToString());
                    MessageBox.Show("App launch failed:\n" + e.ToString());
                    _hasLaunchTask = false;
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
                Debug.WriteLine("Deployment done: " + p);
                src.SetResult(1);
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

        private async Task ReRegisterPackage(string gameDir) {
            foreach (var pkg in new PackageManager().FindPackages(MINECRAFT_PACKAGE_FAMILY)) {
                if (pkg.InstalledLocation.Path == gameDir) {
                    Debug.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + pkg.InstalledLocation.Path);
                    return;
                }
                Debug.WriteLine("Removing package: " + pkg.Id.FullName + " " + pkg.InstalledLocation.Path);
                if (!pkg.IsDevelopmentMode) {
                    BackupMinecraftDataForRemoval();
                    await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, 0));
                } else {
                    Debug.WriteLine("Package is in development mode");
                    await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData));
                }
                Debug.WriteLine("Removal of package done: " + pkg.Id.FullName);
                break;
            }
            Debug.WriteLine("Registering package");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");
            await DeploymentProgressWrapper(new PackageManager().RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode));
            Debug.WriteLine("App re-register done!");
            RestoreMinecraftDataFromReinstall();
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

        private void InvokeRemove(Version v) {
            Directory.Delete(v.GameDirectory, true);
            if(v.UUID == "Unknown") {
                try{
                    _versions.LoadFromCache();
                }
                catch (Exception e) {
                    Debug.WriteLine("List cache load failed:\n" + e.ToString());
                }
            }
            v.UpdateInstallStatus();
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

            ICommand RemoveCommand { get; }

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
