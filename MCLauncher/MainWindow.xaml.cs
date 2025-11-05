using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace MCLauncher {
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Windows.Data;
    using Windows.ApplicationModel;
    using Windows.Foundation;
    using Windows.Management.Core;
    using Windows.Management.Deployment;
    using Windows.Media.Core;
    using Windows.Storage;
    using Windows.System;
    using WPFDataTypes;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, ICommonVersionCommands {

        private static readonly string PREFS_PATH = @"preferences.json";
        private static readonly string IMPORTED_VERSIONS_PATH = @"imported_versions";
        private static readonly string VERSIONS_API_UWP = "https://mrarm.io/r/w10-vdb";
        private static readonly string VERSIONS_API_GDK = "https://raw.githubusercontent.com/MinecraftBedrockArchiver/GdkLinks/refs/heads/master/urls.min.json";

        private VersionList _versions;
        public Preferences UserPrefs { get; }

        private HashSet<CollectionViewSource> _versionListViews = new HashSet<CollectionViewSource>();

        private readonly VersionDownloader _anonVersionDownloader = new VersionDownloader();
        private readonly VersionDownloader _userVersionDownloader = new VersionDownloader();
        private readonly Task _userVersionDownloaderLoginTask;
        private volatile int _userVersionDownloaderLoginTaskStarted;
        private volatile bool _hasLaunchTask = false;
        private volatile bool _hasGdkExtractTask = false;

        public MainWindow() {
            if (File.Exists(PREFS_PATH)) {
                UserPrefs = JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(PREFS_PATH));
            } else {
                UserPrefs = new Preferences();
                RewritePrefs();
            }

            var versionsApiUWP = UserPrefs.VersionsApiUWP != "" ? UserPrefs.VersionsApiUWP : VERSIONS_API_UWP;
            var versionsApiGDK = UserPrefs.VersionsApiGDK != "" ? UserPrefs.VersionsApiGDK : VERSIONS_API_GDK;
            _versions = new VersionList("versions_uwp.json", IMPORTED_VERSIONS_PATH, versionsApiUWP, this, VersionEntryPropertyChanged, "versions_gdk.json", versionsApiGDK);

            InitializeComponent();
            ShowInstalledVersionsOnlyCheckbox.DataContext = this;

            var versionListViewRelease = Resources["versionListViewRelease"] as CollectionViewSource;
            versionListViewRelease.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Release && (v.IsInstalled || v.IsStateChanging || !(ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false));
            });
            versionListViewRelease.Source = _versions;
            ReleaseVersionList.DataContext = versionListViewRelease;
            _versionListViews.Add(versionListViewRelease);

            var versionListViewBeta = Resources["versionListViewBeta"] as CollectionViewSource;
            versionListViewBeta.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Beta && (v.IsInstalled || v.IsStateChanging || !(ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false));
            });
            versionListViewBeta.Source = _versions;
            BetaVersionList.DataContext = versionListViewBeta;
            _versionListViews.Add(versionListViewBeta);

            var versionListViewPreview = Resources["versionListViewPreview"] as CollectionViewSource;
            versionListViewPreview.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Preview && (v.IsInstalled || v.IsStateChanging || !(ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false));
            });
            versionListViewPreview.Source = _versions;
            PreviewVersionList.DataContext = versionListViewPreview;
            _versionListViews.Add(versionListViewPreview);

            var versionListViewImported = Resources["versionListViewImported"] as CollectionViewSource;
            versionListViewImported.Filter += new FilterEventHandler((object sender, FilterEventArgs e) => {
                var v = e.Item as Version;
                e.Accepted = v.VersionType == VersionType.Imported;
            });

            versionListViewImported.Source = _versions;
            ImportedVersionList.DataContext = versionListViewImported;
            _versionListViews.Add(versionListViewImported);

            _userVersionDownloaderLoginTask = new Task(() => {
                _userVersionDownloader.EnableUserAuthorization();
            });
            Dispatcher.Invoke(LoadVersionList);
        }

        private async void LoadVersionList() {
            LoadingProgressLabel.Content = "Loading GDK versions from cache";
            LoadingProgressBar.Value = 1;

            LoadingProgressGrid.Visibility = Visibility.Visible;

            try {
                await _versions.LoadFromCacheGDK();
            } catch (Exception e) {
                Debug.WriteLine("List cache load failed:\n" + e.ToString());
            }

            LoadingProgressLabel.Content = "Downloading new GDK version data";
            LoadingProgressBar.Value = 2;
            try {
                await _versions.DownloadVersionsGDK();
            } catch (Exception e) {
                Debug.WriteLine("List download failed:\n" + e.ToString());
                MessageBox.Show("Failed to update version list from the internet. Some new versions might be missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            LoadingProgressLabel.Content = "Loading UWP versions from cache";
            LoadingProgressBar.Value = 3;
            try {
                await _versions.LoadFromCacheUWP();
            } catch (Exception e) {
                Debug.WriteLine("List cache load failed:\n" + e.ToString());
            }

            LoadingProgressLabel.Content = "Downloading new UWP version data";
            LoadingProgressBar.Value = 4;
            try {
                await _versions.DownloadVersionsUWP();
            } catch (Exception e) {
                Debug.WriteLine("List download failed:\n" + e.ToString());
                MessageBox.Show("Failed to update version list from the internet. Some new versions might be missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            LoadingProgressLabel.Content = "Loading imported versions";
            LoadingProgressBar.Value = 5;
            await _versions.LoadImported();

            LoadingProgressGrid.Visibility = Visibility.Collapsed;
        }

        private void VersionEntryPropertyChanged(object sender, PropertyChangedEventArgs e) {
            RefreshLists();
        }

        private async void ImportButtonClicked(object sender, RoutedEventArgs e) {
            Microsoft.Win32.OpenFileDialog openFileDlg = new Microsoft.Win32.OpenFileDialog();
            openFileDlg.Filter = "XVC and APPX packages (*.msixvc, *.appx)|*.msixvc;*.appx|APPX packages (*.appx)|*.appx|XVC packages (*.msixvc)|*.msixvc|All Files|*.*";
            Nullable<bool> result = openFileDlg.ShowDialog();
            if (result == true) {
                string directory = Path.Combine(IMPORTED_VERSIONS_PATH, openFileDlg.SafeFileName);
                if (Directory.Exists(directory)) {
                    var found = false;
                    foreach (var version in _versions) {
                        if (version.IsImported && version.GameDirectory == directory) {
                            if (version.IsStateChanging) {
                                MessageBox.Show("A version with the same name was already imported, and is currently being modified. Please wait a few moments and try again.", "Error");
                                return;
                            }
                            MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("A version with the same name was already imported. Do you want to delete it ?", "Delete Confirmation", System.Windows.MessageBoxButton.YesNo);
                            if (messageBoxResult == MessageBoxResult.Yes) {
                                var uninstallResult = await Remove(version);
                                if (!uninstallResult) {
                                    MessageBox.Show("Failed to remove existing version. Import aborted.", "Error");
                                    return;
                                }
                                found = true;
                                break;
                            } else {
                                return;
                            }
                        }
                    }
                    if (!found) {
                        MessageBox.Show("The destination path for importing already exists and doesn't contain a Minecraft installation known to the launcher. To avoid loss of data, importing was aborted. Please remove the files manually.", "Error");
                        return;
                    }
                }

                var extension = Path.GetExtension(openFileDlg.FileName).ToLowerInvariant();
                PackageType packageType;
                if (extension == ".msixvc") {
                    packageType = PackageType.GDK;
                } else if (extension == ".appx") {
                    packageType = PackageType.UWP;
                } else {
                    MessageBox.Show("Unsupported file extension: " + extension, "Import failure");
                    return;
                }


                var versionEntry = _versions.AddEntry(openFileDlg.SafeFileName, directory, packageType);

                bool success = false;

                //TODO: for now we don't have any way to know whether these are preview builds or not
                if (packageType == PackageType.UWP) {
                    success = await ExtractAppx(openFileDlg.FileName, directory, versionEntry);
                
                } else if (packageType == PackageType.GDK) {
                    
                    success = await ExtractMsixvc(openFileDlg.FileName, directory, versionEntry, isPreview: false);
                } else {
                    Debug.Assert(false);
                }

                if (success) {
                    versionEntry.UpdateInstallStatus();
                } else {
                    _versions.Remove(versionEntry);
                }
            }
        }

        private void InstallError(string userMessage, string debug, string fileName, Exception ex) {
            string exceptionMessage = "none";
            if (ex != null) {
                Debug.WriteLine(debug + ": " + ex.ToString());
                exceptionMessage = ex.Message;
            } else {
                Debug.WriteLine(debug);
            }

            MessageBox.Show(
                "Failed to import file: " + fileName + "\n\n" +
                userMessage +
                (ex != null ? "\n\nException message: " + exceptionMessage : "") +
                "\n\nCheck the log file if you need more information (File -> Open log file).", "Import failure"
            );
        }

        private async Task<bool> ExtractAppx(string filePath, string directory, Version versionEntry) {
            versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Extracting);
            try {
                await Task.Run(() => {
                    ZipFile.ExtractToDirectory(filePath, directory);
                    File.Delete(Path.Combine(directory, "AppxSignature.p7x"));
                });

                versionEntry.UpdateInstallStatus();

                return true;
            } catch (InvalidDataException ex) {
                InstallError(
                    "File seems to be corrupted or not an APPX file",
                    "Failed extracting appx",
                    filePath,
                    ex
                );
                return false;
            } finally {
                versionEntry.StateChangeInfo = null;
            }
        }

        private async Task<bool> ExtractMsixvc(string filePath, string directory, Version versionEntry, bool isPreview) {
            if (_hasGdkExtractTask) {
                InstallError(
                    "Can't install multiple MSIXVC packages at the same time. Please wait for the current installation to finish before starting a new one.",
                    "Concurrent MSIXVC installation attempt",
                    filePath,
                    null
                );
                return false;
            }
            _hasGdkExtractTask = true;
            // XVC are encrypted containers, I don't currently know of any way to extract them to an arbitrary directory
            // For now we just stage the package in XboxGames, and then move the files to the launcher data directory

            versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Staging);

            var packageManager = new PackageManager();
            var xboxGamesPath = isPreview ? "C:\\XboxGames\\Minecraft Preview for Windows" : "C:\\XboxGames\\Minecraft for Windows";

            //make sure XboxGames is cleared
            Debug.WriteLine("Clearing existing XboxGames Minecraft installation");
            try {
                await UnregisterPackage(versionEntry.GamePackageFamily, versionEntry, skipBackup: false);
            } catch (Exception ex) {
                InstallError(
                    "The existing XboxGames Minecraft installation could not be removed. Please make sure Minecraft is not running and try again.",
                    "Failed clearing XboxGames Minecraft installation",
                    filePath,
                    ex
                );
                return false;
            }

            try {
                await DeploymentProgressWrapper(packageManager.StagePackageAsync(new Uri(filePath), null), versionEntry);
            } catch (Exception ex) {
                InstallError(
                    "File seems to be corrupted or not an MSIXVC file.",
                    "Failed staging MSIXVC",
                    filePath,
                    ex
                );
                return false;
            }

            //TODO: hopefully this never gets put anywhere except C:\, because we don't currently have a way to get the staging location
            var expectedPath = Path.Combine(xboxGamesPath, "Content");
            var exeSrcPath = Path.Combine(expectedPath, "Minecraft.Windows.exe");
            if (!Directory.Exists(expectedPath)) {
                InstallError(
                    "Didn't find installation expected at " + expectedPath + "\nMaybe your XboxGames folder is in a different location?",
                    "Expected XboxGames Minecraft directory not found" + expectedPath,
                    filePath,
                    null
                );
                return false;
            }
            if (!File.Exists(exeSrcPath)) {
                InstallError(
                    "Didn't find Minecraft executable at " + exeSrcPath,
                    "Expected XboxGames Minecraft executable not found: " + exeSrcPath,
                    filePath,
                    null
                );
                return false;
            }

            versionEntry.StateChangeInfo.VersionState = VersionState.Decrypting;

            var exeTmpDir = Path.GetFullPath(@"tmp");
            if (!Directory.Exists(exeTmpDir)) {
                try {
                    Directory.CreateDirectory(exeTmpDir);
                } catch (IOException ex) {
                    InstallError(
                        "The temporary directory for extracting the Minecraft executable could not be created at " + exeTmpDir,
                        "Failed to create tmp dir for exe extraction: " + exeTmpDir,
                        filePath,
                        ex
                    );
                    return false;
                }
            }
            var uuid = Guid.NewGuid().ToString();
            //Use a different tmp path to make sure we don't copy half-done files
            //UUID makes sure we don't copy the leftovers of a different, failed installation
            var exeTmpPath = Path.Combine(exeTmpDir, "Minecraft.Windows_" + uuid + ".exe");
            var exePartialTmpPath = exeTmpPath + ".tmp";

            var exeDstPath = Path.Combine(Path.GetFullPath(directory), "Minecraft.Windows.exe");

            //TODO: these paths probably need to be escaped
            var command = $@"Invoke-CommandInDesktopPackage `
                            -PackageFamilyName ""{versionEntry.GamePackageFamily}"" `
                            -App Game `
                            -Command ""powershell.exe"" `
                            -Args \""-Command Copy-Item '{exeSrcPath}' '{exePartialTmpPath}' -Force; Move-Item '{exePartialTmpPath}' '{exeTmpPath}'\""
                        ";
            Debug.WriteLine("Decrypt command: " + command);

            var processInfo = new ProcessStartInfo {
                FileName = "powershell.exe",
                Arguments = command,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            Debug.WriteLine("Copying decrypted exe");
            try {
                var process = Process.Start(processInfo);
                process.WaitForExit();
                Debug.WriteLine("Process output:" + process.StandardOutput.ReadToEnd());
                Debug.WriteLine("Process errors:" + process.StandardError.ReadToEnd());
            } catch (Exception ex) {
                InstallError(
                    "Failed to run PowerShell to copy the Minecraft executable out of the staged package",
                    "Failed running PowerShell for exe extraction",
                    filePath,
                    ex
                );
                return false;
            }

            for (int i = 0; i < 300 && !File.Exists(exeTmpPath); i++) {
                //Give it up to 30 seconds to copy the file
                //We can't block on the outcome of Invoke-CommandInDesktopPackage, so we have to poll for the file
                //TODO: What if the copy takes longer than that?
                await Task.Delay(100);
            }

            if (!File.Exists(exeTmpPath)) {
                Debug.WriteLine("Src path: " + exeSrcPath);
                Debug.WriteLine("Tmp path: " + exeTmpPath);
                InstallError(
                    "The Minecraft executable could not be copied out of the staged package",
                    "PowerShell subprocess didn't seem to copy the exe in time",
                    filePath,
                    null
                );
                return false;
            }
            Debug.WriteLine("Minecraft executable decrypted successfully");

            versionEntry.StateChangeInfo.VersionState = VersionState.Moving;
            //TODO: this could fail if the launcher is on a different drive than C: ?
            try {
                Debug.WriteLine("Moving staged files");
                Directory.Move(expectedPath, directory);

                Debug.WriteLine("Moving decrypted exe into place");
                File.Delete(exeDstPath);
                File.Move(exeTmpPath, exeDstPath);
            } catch (IOException ex) {
                InstallError(
                    "Failed copying/moving game files to the destination folder",
                    "Failed moving game files to destination",
                    filePath,
                    ex
                );
                return false;
            }

            Debug.WriteLine("Cleaning up XboxGames");
            //we already created a backup earlier, so a new attempt would just get in the way
            await UnregisterPackage(versionEntry.GamePackageFamily, versionEntry, skipBackup: true);

            Debug.WriteLine("Done importing msixvc: " + filePath);

            _hasGdkExtractTask = false;
            return true;
        }

        public ICommand LaunchCommand => new RelayCommand((v) => InvokeLaunch((Version)v));

        public ICommand RemoveCommand => new RelayCommand((v) => InvokeRemove((Version)v));

        public ICommand DownloadCommand => new RelayCommand((v) => InvokeDownload((Version)v));

        private void InvokeLaunch(Version v) {
            if (_hasLaunchTask)
                return;
            _hasLaunchTask = true;
            Task.Run(async () => {
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Registering);
                string gameDir = Path.GetFullPath(v.GameDirectory);
                if (v.PackageType == PackageType.GDK) {
                    //TODO: Not sure if this will work for preview builds
                    //For now we can't register the package with the system because the appx is invalid
                    //Apparently we have to generate a new manifest from the MicrosoftGame.Config file
                    //similar to what wdapp.exe does
                    v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Launching);
                    try {
                        await Task.Run(() => Process.Start(Path.Combine(gameDir, "Minecraft.Windows.exe")));
                        Debug.WriteLine("App launch finished!");
                    } catch (Exception e) {
                        Debug.WriteLine("GDK .exe launch failed:\n" + e.ToString());
                        MessageBox.Show("GDK .exe launch failed:\n" + e.ToString());
                        return;
                    } finally {
                        _hasLaunchTask = false;
                        v.StateChangeInfo = null;
                    }
                } else {
                    try {
                        await ReRegisterPackage(v.GamePackageFamily, gameDir, v);
                    } catch (Exception e) {
                        Debug.WriteLine("App re-register failed:\n" + e.ToString());
                        MessageBox.Show("App re-register failed:\n" + e.ToString());
                        _hasLaunchTask = false;
                        v.StateChangeInfo = null;
                        return;
                    }
                    v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Launching);
                    try {
                        var pkg = await AppDiagnosticInfo.RequestInfoForPackageAsync(v.GamePackageFamily);
                        if (pkg.Count > 0)
                            await pkg[0].LaunchAsync();
                        Debug.WriteLine("App launch finished!");
                    } catch (Exception e) {
                        Debug.WriteLine("App launch failed:\n" + e.ToString());
                        MessageBox.Show("App launch failed:\n" + e.ToString());
                        return;
                    } finally {
                        _hasLaunchTask = false;
                        v.StateChangeInfo = null;
                    }
                }
            });
        }

        private async Task DeploymentProgressWrapper(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> t, Version version) {
            TaskCompletionSource<int> src = new TaskCompletionSource<int>();
            t.Progress += (v, p) => {
                Debug.WriteLine("Deployment progress: " + p.state + " " + p.percentage + "%");
            };
            t.Completed += (v, p) => {
                if (p == AsyncStatus.Error) {
                    Debug.WriteLine("Deployment failed: " + v.GetResults().ErrorText + " (error code " + v.GetResults().ExtendedErrorCode.HResult + ")");
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

        private void BackupMinecraftDataForRemoval(string packageFamily) {
            ApplicationData data;
            try {
                data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            }catch (FileNotFoundException e) {
                Debug.WriteLine("BackupMinecraftDataForRemoval: Application data not found for package family " + packageFamily + ": " + e.ToString());
                Debug.WriteLine("Hopefully this means there's no data to back up ???");
                return;
            }
            string tmpDir = GetBackupMinecraftDataDir();
            if (Directory.Exists(tmpDir)) {
                //TODO: this might happen if two different versions
                //try to be uninstalled at the same time???
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

        private void RestoreMinecraftDataFromReinstall(string packageFamily) {
            string tmpDir = GetBackupMinecraftDataDir();
            if (!Directory.Exists(tmpDir))
                return;
            var data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            Debug.WriteLine("Moving backup Minecraft data to: " + data.LocalFolder.Path);
            RestoreMove(tmpDir, data.LocalFolder.Path);
            Directory.Delete(tmpDir, true);
        }

        private async Task RemovePackage(Package pkg, string packageFamily, Version version, bool skipBackup) {
            Debug.WriteLine("Removing package: " + pkg.Id.FullName);
            if (!pkg.IsDevelopmentMode) {
                if (!skipBackup) {
                    BackupMinecraftDataForRemoval(packageFamily);
                }
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, 0), version);
            } else {
                Debug.WriteLine("Package is in development mode");
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData), version);
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

        private async Task UnregisterPackage(string packageFamily, Version version, bool skipBackup) {
            foreach (var pkg in new PackageManager().FindPackages(packageFamily)) {
                string location = GetPackagePath(pkg);
                Debug.WriteLine("Removing package: " + pkg.Id.FullName + " " + location);
                await RemovePackage(pkg, packageFamily, version, skipBackup);
            }
        }

        private async Task ReRegisterPackage(string packageFamily, string gameDir, Version version) {
            foreach (var pkg in new PackageManager().FindPackages(packageFamily)) {
                string location = GetPackagePath(pkg);
                if (location == gameDir) {
                    Debug.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + location);
                    return;
                }
                await RemovePackage(pkg, packageFamily, version, skipBackup: false);
            }
            Debug.WriteLine("Registering package");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");
            Debug.WriteLine("Manifest path: " + manifestPath);
            await DeploymentProgressWrapper(new PackageManager().RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode), version);
            Debug.WriteLine("App re-register done!");
            RestoreMinecraftDataFromReinstall(packageFamily);
        }

        private void InvokeDownload(Version v) {
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            v.IsNew = false;
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Initializing);
            v.StateChangeInfo.CancelCommand = new RelayCommand((o) => cancelSource.Cancel());

            Debug.WriteLine("Download start");
            Task.Run(async () => {
                string dlPath = Path.GetFullPath((v.VersionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + v.Name + (v.PackageType == PackageType.UWP ? ".Appx" : ".msixvc"));
                VersionDownloader downloader = _anonVersionDownloader;

                VersionDownloader.DownloadProgress dlProgressHandler = (current, total) => {
                    if (v.StateChangeInfo.VersionState != VersionState.Downloading) {
                        Debug.WriteLine("Actual download started");
                        v.StateChangeInfo.VersionState = VersionState.Downloading;
                        if (total.HasValue)
                            v.StateChangeInfo.MaxProgress = total.Value;
                    }
                    v.StateChangeInfo.Progress = current;
                };

                try {
                    if (v.PackageType == PackageType.UWP) {
                        await downloader.DownloadAppx(v.UUID, "1", dlPath, dlProgressHandler, cancelSource.Token);
                    } else if (v.PackageType == PackageType.GDK) {
                        await downloader.DownloadMsixvc(v.DownloadURLs, dlPath, dlProgressHandler, cancelSource.Token);
                    } else {
                        throw new Exception("Unknown package type");
                    }
                    Debug.WriteLine("Download complete");
                } catch (BadUpdateIdentityException) {
                    Debug.WriteLine("Download failed due to failure to fetch download URL");
                    MessageBox.Show(
                        "Unable to fetch download URL for version." +
                        (v.VersionType == VersionType.Beta ? "\nFor beta versions, please make sure your account is subscribed to the Minecraft beta programme in the Xbox Insider Hub app." : "")
                    );
                    v.StateChangeInfo = null;
                    return;
                } catch (Exception e) {
                    Debug.WriteLine("Download failed:\n" + e.ToString());
                    if (!(e is TaskCanceledException))
                        MessageBox.Show("Download failed:\n" + e.ToString());
                    v.StateChangeInfo = null;
                    return;
                }
                try {
                    string dirPath = v.GameDirectory;
                    if (Directory.Exists(dirPath))
                        Directory.Delete(dirPath, true);
                    if (v.PackageType == PackageType.UWP) {
                        await ExtractAppx(dlPath, dirPath, v);
                    } else if (v.PackageType == PackageType.GDK) {
                        await ExtractMsixvc(dlPath, dirPath, v, isPreview: v.VersionType == VersionType.Preview);
                    } else {
                        throw new Exception("Unknown package type");
                    }
                    if (UserPrefs.DeleteAppxAfterDownload) {
                        Debug.WriteLine("Deleting package to reduce disk usage");
                        File.Delete(dlPath);
                    } else {
                        Debug.WriteLine("Not deleting package due to user preferences");
                    }
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

        private async Task<bool> Remove(Version v) {
            try {
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Unregistering);
                Debug.WriteLine("Unregistering version " + v.DisplayName);
                try {
                    await UnregisterPackage(v.GamePackageFamily, v, skipBackup: false);
                } catch (Exception e) {
                    Debug.WriteLine("Failed unregistering package:\n" + e.ToString());
                    MessageBox.Show("Failed unregistering package:\n" + e.ToString(), "Uninstall error");
                    return false;
                }
                Debug.WriteLine("Cleaning up game files for version " + v.DisplayName);
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.CleaningUp);
                try {
                    // Use the \\?\ prefix to support long paths
                    Directory.Delete(@"\\?\" + Path.GetFullPath(v.GameDirectory), true);
                } catch (Exception e) {
                    Debug.WriteLine("Failed deleting game directory:\n" + e.ToString());
                    MessageBox.Show("Failed deleting game directory:\n" + e.ToString(), "Uninstall error");
                    return false;
                }

                if (v.IsImported) {
                    Dispatcher.Invoke(() => _versions.Remove(v));
                    Debug.WriteLine("Removed imported version " + v.DisplayName);
                } else {
                    v.UpdateInstallStatus();
                    Debug.WriteLine("Removed release version " + v.DisplayName);
                }

                return true;
            } finally { 
                v.StateChangeInfo = null;
            }
        }

        private void InvokeRemove(Version v) {
            Task.Run(async () => await Remove(v));
        }

        private void ShowInstalledVersionsOnlyCheckbox_Changed(object sender, RoutedEventArgs e) {
            UserPrefs.ShowInstalledOnly = ShowInstalledVersionsOnlyCheckbox.IsChecked ?? false;
            RefreshLists();
            RewritePrefs();
        }

        private void RefreshLists() {
            Dispatcher.Invoke(() => {
                foreach (var list in _versionListViews) {
                    list.View.Refresh();
                }
            });
        }

        private void DeleteAppxAfterDownloadCheck_Changed(object sender, RoutedEventArgs e) {
            UserPrefs.DeleteAppxAfterDownload = DeleteAppxAfterDownloadOption.IsChecked;
        }

        private void RewritePrefs() {
            File.WriteAllText(PREFS_PATH, JsonConvert.SerializeObject(UserPrefs));
        }

        private void MenuItemOpenLogFileClicked(object sender, RoutedEventArgs e) {
            if (!File.Exists(@"Log.txt")) {
                MessageBox.Show("Log file not found", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            } else 
                Process.Start(@"Log.txt");
        }

        private void MenuItemOpenDataDirClicked(object sender, RoutedEventArgs e) {
            Process.Start(@"explorer.exe", Directory.GetCurrentDirectory());
        }

        private void MenuItemCleanupForMicrosoftStoreReinstallClicked(object sender, RoutedEventArgs e) {
            var result = MessageBox.Show(
                "Versions of Minecraft installed by the launcher will be uninstalled.\n" +
                    "This will allow you to reinstall Minecraft from Microsoft Store. Your data (worlds, etc.) won't be removed.\n\n" +
                    "Are you sure you want to continue?",
                "Uninstall all versions",
                MessageBoxButton.OKCancel
            );
            if (result == MessageBoxResult.OK) {
                Debug.WriteLine("Starting uninstall of ALL versions!");
                foreach (var version in _versions) {
                    if (version.IsInstalled) {
                        InvokeRemove(version);
                    }
                }
                Debug.WriteLine("Scheduled uninstall of ALL versions.");
            }
        }

        private void MenuItemRefreshVersionListClicked(object sender, RoutedEventArgs e) {
            Dispatcher.Invoke(LoadVersionList);
        }

        private void onEndpointChangedHandler(object sender, string newEndpoint) {
            UserPrefs.VersionsApiUWP = newEndpoint;
            _versions.VersionsApiUWP = newEndpoint == "" ? VERSIONS_API_UWP : newEndpoint;
            Dispatcher.Invoke(LoadVersionList);
            RewritePrefs();
        }

        private void MenuItemSetVersionListEndpointClicked(object sender, RoutedEventArgs e) {
            var dialog = new VersionListEndpointDialog(UserPrefs.VersionsApiUWP) {
                Owner = this
            };
            dialog.OnEndpointChanged += onEndpointChangedHandler;

            dialog.Show();
        }
    }

    struct MinecraftPackageFamilies
    {
        public static readonly string MINECRAFT = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        public static readonly string MINECRAFT_PREVIEW = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";
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

        public enum VersionType : int
        {
            Release = 0,
            Beta = 1,
            Preview = 2,
            Imported = 100
        }

        public enum PackageType {
            UWP,
            GDK
        }

        public class Version : NotifyPropertyChangedBase {
            public static readonly string UNKNOWN_UUID = "UNKNOWN";

            public Version(string uuid, string name, VersionType versionType, bool isNew, ICommonVersionCommands commands, PackageType packageType, List<string> downloadUrls) {
                this.UUID = uuid;
                this.Name = name;
                this.VersionType = versionType;
                this.IsNew = isNew;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
                this.RemoveCommand = commands.RemoveCommand;
                this.GameDirectory = (versionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + Name;
                this.PackageType = packageType;
                this.DownloadURLs = downloadUrls ?? new List<string>();
            }
            public Version(string name, string directory, ICommonVersionCommands commands, PackageType packageType) {
                this.UUID = UNKNOWN_UUID;
                this.Name = name;
                this.VersionType = VersionType.Imported;
                this.DownloadCommand = commands.DownloadCommand;
                this.LaunchCommand = commands.LaunchCommand;
                this.RemoveCommand = commands.RemoveCommand;
                this.GameDirectory = directory;
                this.PackageType = packageType;
            }

            public string UUID { get; set; }
            public string Name { get; set; }
            public VersionType VersionType { get; set; }
            public bool IsNew {
                get { return _isNew; }
                set {
                    _isNew = value;
                    OnPropertyChanged("IsNew");
                }
            }
            public bool IsImported {
                get => VersionType == VersionType.Imported;
            }

            public string GameDirectory { get; set; }

            public PackageType PackageType { get; set; }

            public List<string> DownloadURLs { get; set; }

            public string GamePackageFamily
            {
                get => VersionType == VersionType.Preview ? MinecraftPackageFamilies.MINECRAFT_PREVIEW : MinecraftPackageFamilies.MINECRAFT;
            }

            public bool IsInstalled => Directory.Exists(GameDirectory);

            public string DisplayName {
                get {
                    string typeTag = "";
                    if (VersionType == VersionType.Beta)
                        typeTag = "(beta)";
                    else if (VersionType == VersionType.Preview)
                        typeTag = "(preview)";
                    string packageTypeTag = "";
                    if (PackageType == PackageType.GDK) {
                        packageTypeTag += "GDK";
                    } else if (PackageType == PackageType.UWP) {
                        packageTypeTag += "UWP";
                    }

                    return Name + " - " + packageTypeTag + (typeTag.Length > 0 ? " " + typeTag : "") + (IsNew ? " (NEW!)" : "");
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
            private bool _isNew = false;
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
            Registering,
            Launching,
            Unregistering,
            CleaningUp,
            Staging,
            Decrypting,
            Moving
        };

        public class VersionStateChangeInfo : NotifyPropertyChangedBase {

            private VersionState _versionState;

            private long _progress = 0;
            private long _maxProgress = 0;

            public VersionStateChangeInfo(VersionState versionState) {
                _versionState = versionState;
            }

            public VersionState VersionState {
                get { return _versionState; }
                set {
                    _versionState = value;
                    Progress = 0;
                    MaxProgress = 0;
                    OnPropertyChanged("IsProgressIndeterminate");
                    OnPropertyChanged("DisplayStatus");
                }
            }

            public bool IsProgressIndeterminate {
                get {
                    return _maxProgress == 0;
                }
            }

            public long Progress {
                get { return _progress; }
                set { _progress = value; OnPropertyChanged("Progress"); OnPropertyChanged("DisplayStatus"); }
            }

            public long MaxProgress {
                get { return _maxProgress; }
                set { _maxProgress = value; OnPropertyChanged("MaxProgress"); OnPropertyChanged("DisplayStatus"); OnPropertyChanged("IsProgressIndeterminate"); }
            }

            public string DisplayStatus {
                get {
                    switch (_versionState) {
                        case VersionState.Initializing: return "Preparing...";
                        case VersionState.Downloading:
                            return "Downloading... " + (Progress / 1024 / 1024) + "MiB/" + (MaxProgress / 1024 / 1024) + "MiB";
                        case VersionState.Extracting: return "Extracting...";
                        case VersionState.Registering: return "Registering package...";
                        case VersionState.Launching: return "Launching...";
                        case VersionState.Unregistering: return "Unregistering package...";
                        case VersionState.CleaningUp: return "Cleaning up...";
                        case VersionState.Staging: return "Staging package... (this might take a few minutes)";
                        case VersionState.Decrypting: return "Copying decrypted Minecraft.Windows.exe...";
                        case VersionState.Moving: return "Copying other game files...";
                        default: return "Wtf is happening? ...";
                    }
                }
            }

            public ICommand CancelCommand { get; set; }

        }

    }
}
