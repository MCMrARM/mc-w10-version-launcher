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
    using System.Linq;
    using System.Threading;
    using System.Windows.Data;
    using Windows.ApplicationModel;
    using Windows.Foundation;
    using Windows.Management.Core;
    using Windows.Management.Deployment;
    using Windows.Storage;
    using Windows.System;
    using Windows.UI.Xaml.Controls;
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
            _versions.PrepareForReload();

            LoadingProgressLabel.Content = "Loading GDK versions from cache";
            LoadingProgressBar.Value = 1;

            LoadingProgressGrid.Visibility = Visibility.Visible;

            try {
                await _versions.LoadFromCacheGDK();
            } catch (Exception e) {
                Debug.WriteLine("List cache load failed:\n" + e.ToString());
            }

            LoadingProgressLabel.Content = "Loading UWP versions from cache";
            LoadingProgressBar.Value = 2;
            try {
                await _versions.LoadFromCacheUWP();
            } catch (Exception e) {
                Debug.WriteLine("List cache load failed:\n" + e.ToString());
            }

            _versions.PrepareForReload();

            LoadingProgressLabel.Content = "Downloading new GDK version data";
            LoadingProgressBar.Value = 3;
            try {
                await _versions.DownloadVersionsGDK();
            } catch (Exception e) {
                Debug.WriteLine("List download failed:\n" + e.ToString());
                MessageBox.Show("Failed to update version list from the internet. Some new versions might be missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    ShowGDKFirstUseWarning();
                    success = await ExtractMsixvc(openFileDlg.FileName, directory, versionEntry, isPreview: false);
                } else {
                    Debug.Assert(false);
                }

                if (success) {
                    versionEntry.StateChangeInfo = null;
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

        private void ShowGDKFirstUseWarning() {
            if (!UserPrefs.HasPreviouslyUsedGDK) {
                MessageBox.Show(
                    "The launcher has detected that you have never used a GDK version of Minecraft before.\n" +
                        "Please be aware of the following:\n\n" +
                        "You MUST install a GDK version of Minecraft from the Store before attempting to use the launcher for GDK versions." +
                        "This is because the launcher needs the Store to install the keys to decrypt the installation packages.\n" +
                        "If you don't, the installation packages may show corruption messages.\n\n" +
                        "It is STRONGLY recommended to add an exclusion for C:\\XboxGames (or wherever your games install by default) to Windows Defender, " +
                        "otherwise the installation process will take 10x as long.\n\n" +
                        "During installation, you will see a few dialog boxes and a PowerShell window briefly pop up.\n" +
                        "This is normal and is an unavoidable consequence of the installation method used for GDK versions.\n\n" +
                        "Please also note that the location of your worlds will change when moving from UWP to GDK and vice versa.\n" +
                        "If you can't find your worlds, you can use File -> \"Find my data\" to locate them.",
                    "Minecraft GDK warning"
                );
                UserPrefs.HasPreviouslyUsedGDK = true;
                RewritePrefs();
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
            try {
                // XVC are encrypted containers, I don't currently know of any way to extract them to an arbitrary directory
                // For now we just stage the package in XboxGames, and then move the files to the launcher data directory

                versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Staging);

                var packageManager = new PackageManager();

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
                        "Failed to stage package.\n" +
                            "This may mean that the file is damaged, not an MSIXVC file. Please check the integrity of the file.\n\n" +
                            "However, this error might also happen if you've never installed a GDK version of Minecraft from the Store before,\n" +
                            "as the launcher relies on the Store to install the keys needed to decrypt the installation package.\n" +
                            "Please ensure that you've installed " + (isPreview ? "Minecraft Preview" : "Minecraft") + " from the Store before installing GDK versions using the launcher.",
                        "Failed staging MSIXVC",
                        filePath,
                        ex
                    );
                    return false;
                }

                string installPath = "";
                foreach (var pkg in new PackageManager().FindPackages(versionEntry.GamePackageFamily)) {
                    if (installPath != "") {
                        InstallError(
                            "Minecraft is installed in multiple places, and the launcher doesn't know where to copy files from.\n" +
                            "This is probably because another user has the game installed.",
                            "Multiple locations found for staged MSIXVC: " + installPath + ", " + pkg.InstalledLocation.Path,
                            filePath,
                            null
                        );
                        return false;
                    }
                    installPath = pkg.InstalledLocation.Path;
                }
                Debug.WriteLine("Detected staging path: " + installPath);
                string resolvedPath = LinkResolver.Resolve(installPath);
                Debug.WriteLine("Symlink resolved as " + resolvedPath);
                installPath = resolvedPath;

                var exeSrcPath = Path.Combine(installPath, "Minecraft.Windows.exe");
                if (!Directory.Exists(installPath)) {
                    InstallError(
                        "Didn't find installation expected at " + installPath + "\nMaybe your XboxGames folder is in a different location?",
                        "Expected XboxGames Minecraft directory not found" + installPath,
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
                        "The Minecraft executable could not be copied out of the staged package.\n" +
                            "This is usually due to the game license not being installed for your Windows user account.\n\n" +
                            "Please ensure that you've installed " + (isPreview ? "Minecraft Preview" : "Minecraft") + " from the Store before using this launcher.",
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
                    Directory.Move(installPath, directory);

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
                return true;

            } finally {
                _hasGdkExtractTask = false;
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
                v.StateChangeInfo = new VersionStateChangeInfo(VersionState.MovingData);
                if (!MoveMinecraftData(v.GamePackageFamily, v.PackageType)) {
                    Debug.WriteLine("Data restore error, aborting launch");
                    v.StateChangeInfo = null;
                    _hasLaunchTask = false;
                    return;
                }
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

        private int GetWorldCountInDataDir(string dataDir) {
            var worldsFolder = Path.Combine(dataDir, "games", "com.mojang", "minecraftWorlds");
            if (!Directory.Exists(worldsFolder)) {
                return 0;
            }
            return Directory.GetDirectories(worldsFolder).Length;
        }

        private Dictionary<string, int> LocateMinecraftWorlds(string packageFamily) {
            List<string> candidates = new List<string>();

            var uwpDataDir = GetMinecraftUWPDataDir(packageFamily);
            if (uwpDataDir != "") {
                candidates.Add(uwpDataDir);
            }

            candidates.AddRange(GetMinecraftGDKDataDirs(packageFamily));
            candidates.Add(GetBackupMinecraftDataDir());

            var worldLocations = new Dictionary<string, int>();
            foreach(var dataDir in candidates) {
                Debug.WriteLine("Checking for worlds in: " + dataDir);
                var worldsFolder = Path.Combine(dataDir, "games", "com.mojang", "minecraftWorlds");
                if (!Directory.Exists(worldsFolder)) {
                    Debug.WriteLine("No worlds found in: " + worldsFolder);
                    continue;
                }
                int worlds = Directory.GetDirectories(worldsFolder).Length;
                if (worlds > 0) {
                    worldLocations[dataDir] = worlds;
                    Debug.WriteLine("Found " + worlds + " worlds in: " + worldsFolder);
                } else {
                    Debug.WriteLine("No worlds found in: " + worldsFolder);
                }
            }

            return worldLocations;
        }

        private string GetBackupMinecraftDataDir() {
            //TODO: this really ought to be separated by package family
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tmpDir = Path.Combine(localAppData, "TmpMinecraftLocalState");
            return tmpDir;
        }

        private string GetMinecraftUWPRootDir(string packageFamily) {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                packageFamily
            );
        }

        private string GetMinecraftUWPDataDir(string packageFamily) {
            return Path.Combine(GetMinecraftUWPRootDir(packageFamily), "LocalState");
        }

        private string GetMinecraftGDKRootDir(string packageFamily) {
            string infix;
            switch(packageFamily) {
                case MinecraftPackageFamilies.MINECRAFT:
                    infix = "Minecraft Bedrock";
                    break;
                case MinecraftPackageFamilies.MINECRAFT_PREVIEW:
                    infix = "Minecraft Bedrock Preview";
                    break;
                default: throw new ArgumentException("Invalid Minecraft package family: " + packageFamily);
            }
            var gdkRootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                infix
            );
            return gdkRootDir;
        }

        private List<string> GetMinecraftGDKDataDirs(string packageFamily) {
            var parentDir = Path.Combine(
                GetMinecraftGDKRootDir(packageFamily),
                "Users"
            );
            var results = new List<string>();

            if (!Directory.Exists(parentDir)) {
                Debug.WriteLine("GDK Users directory doesn't exist: " + parentDir);
                return results;
            }

            results.AddRange(Directory.EnumerateDirectories(parentDir));

            return results;
        }

        private bool BackupMinecraftDataForRemoval(string packageFamily) {
            ApplicationData data;
            try {
                data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            }catch (FileNotFoundException e) {
                Debug.WriteLine("BackupMinecraftDataForRemoval: Application data not found for package family " + packageFamily + ": " + e.ToString());
                Debug.WriteLine("This should mean the package isn't installed, so we don't need to backup the data");
                return true;
            }
            if (!Directory.Exists(data.LocalFolder.Path)) {
                //this is fine only for GDK versions
                Debug.WriteLine("LocalState folder " + data.LocalFolder.Path + " doesn't exist, so it can't be backed up");
                return true;
            }
            string tmpDir = GetBackupMinecraftDataDir();
            if (Directory.Exists(tmpDir)) {
                if (GetWorldCountInDataDir(tmpDir) > 0) {
                    //TODO: this might happen if two different versions
                    //try to be uninstalled at the same time???
                    Debug.WriteLine("BackupMinecraftDataForRemoval error: " + tmpDir + " already exists");
                    Process.Start("explorer.exe", tmpDir);
                    MessageBox.Show("The temporary directory for backing up MC data already exists. This probably means that we failed last time backing up the data. Please back the directory up manually.");
                    return false;
                }
                Directory.Delete(tmpDir, recursive: true);
            }
            Debug.WriteLine("Moving Minecraft data to: " + tmpDir);
            Directory.Move(data.LocalFolder.Path, tmpDir);

            return true;
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

        private bool RestoreUWPData(string src, string uwpDataDir, string uwpParent) {
            Debug.WriteLine("Restoring Minecraft data from src dir " + src + " to " + uwpDataDir);
            try {
                if (Directory.Exists(uwpDataDir)) {
                    Debug.WriteLine("Deleting: " + uwpDataDir);
                    Directory.Delete(uwpDataDir, recursive: true);
                }
                if (!Directory.Exists(uwpParent)) {
                    Debug.WriteLine("Creating parent dir: " + uwpParent);
                    Directory.CreateDirectory(uwpParent);
                }
                Debug.WriteLine("Restoring files");
                RestoreMove(src, uwpDataDir);
                Debug.WriteLine("Deleting src dir: " + src);
                Directory.Delete(src, true);
                Debug.WriteLine("Restore complete");
                return true;
            } catch (Exception e) {
                Debug.WriteLine("Failed restoring Minecraft data from " + src + ": " + e.ToString());
                MessageBox.Show("Failed to move Minecraft data from:\n"
                    + src
                    + "\nto:\n"
                    + uwpDataDir
                    + "\n\nCheck the log file for more information.", "Data restore error"
                );
                return false;
            }
        }

        private bool MoveMinecraftData(string packageFamily, PackageType destinationType) {
            var dataLocations = LocateMinecraftWorlds(packageFamily);
            if (dataLocations.Count == 0) {
                Debug.WriteLine("No Minecraft data found to restore or link");
                return true;
            }

            if (dataLocations.Count > 1) {
                var messageString = "";
                foreach (var loc in dataLocations) {
                    messageString += $"\n - {loc.Key}: {loc.Value} worlds";
                }
                Debug.WriteLine("Can't automatically restore Minecraft data - multiple locations with worlds found:" + messageString);
                MessageBox.Show(
                    "Unable to automatically restore Minecraft worlds for UWP, because multiple locations with worlds were found:"
                        + messageString
                        + "\n\nPlease resolve the conflicts manually by copying worlds into the desired location.",
                    "Data restore error"
                );
                return false;
            }

            string dataLocation = dataLocations.Keys.First();

            string tmpDir = GetBackupMinecraftDataDir();
            string uwpDataDir = GetMinecraftUWPDataDir(packageFamily);
            string uwpParent = GetMinecraftUWPRootDir(packageFamily);
            if (dataLocation == tmpDir) {
                //we don't know where GDK will want to store this due to the user folder names containing some kind of UID
                //so we restore to UWP location and let Minecraft handle the GDK migration by itself
                Debug.WriteLine("Restoring Minecraft data from backup dir " + tmpDir + " to " + uwpDataDir);
                if (!RestoreUWPData(tmpDir, uwpDataDir, uwpParent)) {
                    return false;
                }
                dataLocation = uwpDataDir;
            }

            if (destinationType == PackageType.GDK && dataLocation == uwpDataDir) {
                //TODO: not sure it's a good idea to let the game migrate UWP data on its own,
                //considering how many people have had problems with it???
                Debug.WriteLine("Deleting uwpMigration.dat, so GDK Minecraft will migrate data from UWP next time it's used");
                var uwpMigrationDat = Path.Combine(
                    GetMinecraftGDKRootDir(packageFamily),
                    "games",
                    "com.mojang",
                    "uwpMigration.dat"
                );
                Debug.WriteLine("uwpMigration.dat path: " + uwpMigrationDat);
                try {
                    File.Delete(uwpMigrationDat);
                    return true;
                } catch (Exception e) {
                    Debug.WriteLine("Failed deleting uwpMigration.dat: " + e.ToString());
                    MessageBox.Show(
                        "Failed deleting uwpMigration.dat file.\n" +
                        "Your worlds will be visible to UWP versions, but GDK versions won't see them unless you move them back manually.\n\n" +
                        "Please delete the following file manually: " + uwpMigrationDat +
                        "\n\nAlternatively, you can copy your worlds back to the GDK folder next time you run a GDK version." +
                        "\nYour worlds are currently located at: " + uwpDataDir +
                        "Data migration notice"
                    );
                    return false;
                }
            } else if (destinationType == PackageType.UWP && dataLocation != uwpDataDir) {
                //this should mean that we found data in a GDK location, so move it for UWP
                var gdkDataDir = dataLocations.Keys.First();
                if (!RestoreUWPData(gdkDataDir, uwpDataDir, uwpParent)) {
                    return false;
                }

                return true;
            } else {
                Debug.WriteLine("Minecraft data already in the right place " + dataLocation);
                return true;
            }
        }

        private async Task RemovePackage(Package pkg, string packageFamily, Version version, bool skipBackup) {
            Debug.WriteLine("Removing package: " + pkg.Id.FullName);
            if (!pkg.IsDevelopmentMode) {
                if (!skipBackup) {
                    //TODO: It would be nice to skip this if we're uninstalling a GDK version
                    //however, the package being removed may not be the one passed in the version parameter
                    //since the version parameter is only used for displaying UI status
                    if (!BackupMinecraftDataForRemoval(packageFamily)) {
                        throw new Exception("Failed backing up Minecraft data before uninstalling package");
                    }
                }
                //TODO: this will bomb data for other users. We only currently backup data for the current user
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.RemoveForAllUsers), version);
            } else {
                Debug.WriteLine("Package is in development mode");
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData | RemovalOptions.RemoveForAllUsers), version);
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
                        ShowGDKFirstUseWarning();
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

        private void MenuItemUninstallAllVersionsClicked(object sender, RoutedEventArgs e) {
            var result = MessageBox.Show(
                "All versions of Minecraft managed by the launcher will be unregistered and deleted.\n" +
                    "Your data (worlds, etc.) won't be removed.\n\n" +
                    "Note: If you just want to reinstall Minecraft from the Store, and don't want to delete your launcher-managed versions, you can use the \"Cleanup for Store reinstall\" option instead.\n\n" +
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

        private void onEndpointChangedHandler(object sender, string newUwpIdsEndpoint, string newGdkPackageUrlsEndpoint) {
            UserPrefs.VersionsApiUWP = newUwpIdsEndpoint == "" ? VERSIONS_API_UWP : newUwpIdsEndpoint;
            UserPrefs.VersionsApiGDK = newGdkPackageUrlsEndpoint == "" ? VERSIONS_API_GDK : newGdkPackageUrlsEndpoint;
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

        private string buildDataLocationMessage(string displayName, string packageFamily) {
            var message = "Data for " + displayName + ":";
            var locations = LocateMinecraftWorlds(packageFamily);
            if (locations.Count == 0) {
                return message + "\n - (no folders with worlds found)";
            }

            foreach (var loc in locations) {
                message += $"\n - {loc.Value} worlds found in {loc.Key}";
            }
            return message;
        }

        private void MenuItemFindMyDataClicked(object sender, RoutedEventArgs e) {
            var locations = LocateMinecraftWorlds(MinecraftPackageFamilies.MINECRAFT);

            MessageBox.Show(
                buildDataLocationMessage("Release", MinecraftPackageFamilies.MINECRAFT) + "\n\n" +
                buildDataLocationMessage("Preview", MinecraftPackageFamilies.MINECRAFT_PREVIEW) + "\n\n" +
                "Note: Data folders containing no worlds are not shown.",
                "Minecraft data locations"
            );
        }

        private async void MenuItemCleanupForStoreInstallClicked(object sender, RoutedEventArgs e) {
            var dialog = new ProgressDialog();
            dialog.Owner = this;
            bool allowClose = false;
            dialog.Closing += (object sender_, CancelEventArgs e_) => {
                if (!allowClose) {
                    e_.Cancel = true;
                }
            };

            dialog.Show();

            Debug.WriteLine("Cleaning up system");
            try {
                await UnregisterPackage(MinecraftPackageFamilies.MINECRAFT, null, skipBackup: false);
                await UnregisterPackage(MinecraftPackageFamilies.MINECRAFT_PREVIEW, null, skipBackup: false);
            } catch (Exception ex) {
                Debug.WriteLine("Error cleaning up: " + ex.Message);
                MessageBox.Show("An error occurred while cleaning up. Check the log for details.", "Error");
            }
            Debug.WriteLine("Done cleaning up");
            allowClose = true;
            dialog.Close();
            MessageBox.Show("Cleanup completed. You should now be able to install Minecraft from Microsoft Store.", "Cleanup completed");
        }
    }

    struct MinecraftPackageFamilies
    {
        public const string MINECRAFT = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        public const string MINECRAFT_PREVIEW = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";
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
            Moving,
            MovingData
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
                        case VersionState.MovingData: return "Restoring Minecraft worlds...";
                        default: return "Wtf is happening? ...";
                    }
                }
            }

            public ICommand CancelCommand { get; set; }

        }

    }
}
