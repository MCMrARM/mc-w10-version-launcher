using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Core;
using Windows.Management.Deployment;
using MCLauncher.WPFDataTypes;

namespace MCLauncher
{
    class VersionManager
    {
        private readonly VersionList _versions;
        private readonly Preferences _userPrefs;
        private readonly string _importedDirectory;

        private readonly VersionDownloader _anonVersionDownloader = new VersionDownloader();
        private readonly VersionDownloader _userVersionDownloader = new VersionDownloader();
        private Task _userVersionDownloaderLoginTask;
        private volatile bool _hasLaunchTask;

        private readonly Dispatcher _dispatcher;

        public VersionManager(VersionList versions, Preferences userPrefs, Dispatcher dispatcher, string importedDirectory)
        {
            _versions = versions;
            _userPrefs = userPrefs;
            _dispatcher = dispatcher;
            _importedDirectory = importedDirectory;
        }

        public async Task DetectPreviewGdkInstallAsync()
        {
            try
            {
                bool previewInstalled = false;
                bool retailInstalled = false;

                // Run potentially slow detection (process spawn, disk checks) on a background thread
                await Task.Run(() =>
                {
                    previewInstalled = GdkInstaller.IsInstalled(true);
                    retailInstalled = GdkInstaller.IsInstalled(false);
                });

                // Update bound version properties back on the captured context (UI thread)
                foreach (var ver in _versions.Where(v => v.VersionType == VersionType.Preview))
                {
                    ver.IsGdkInstalled = previewInstalled;
                }
                foreach (var ver in _versions.Where(v => v.VersionType == VersionType.Release))
                {
                    if (retailInstalled) ver.IsGdkInstalled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DetectPreviewGdkInstall error: " + ex.Message);
            }
        }

        public async Task ImportPackageAsync(string packagePath, string safeFileName)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(safeFileName))
                return;

            string ext = Path.GetExtension(packagePath).ToLowerInvariant();
            if (ext == ".msixvc")
            {
                await ImportGdkPackageAsync(packagePath);
                return;
            }

            string directory = Path.Combine(_importedDirectory, safeFileName);
            if (!await EnsureImportDirectoryIsUsableAsync(directory))
                return;

            await ExtractImportedPackageAsync(packagePath, safeFileName, directory);
        }

        private async Task ImportGdkPackageAsync(string packagePath)
        {
            await Task.Run(async () =>
            {
                try
                {
                    await GdkInstaller.InstallMsixvc(packagePath, true);
                    _dispatcher.Invoke(() => MessageBox.Show("GDK install completed successfully."));
                }
                catch (Exception ge)
                {
                    Debug.WriteLine("GDK install failed:\n" + ge);
                    _dispatcher.Invoke(() => MessageBox.Show("GDK install failed:\n" + ge, "Import failure"));
                }
            });
        }

        private async Task<bool> EnsureImportDirectoryIsUsableAsync(string directory)
        {
            if (!Directory.Exists(directory))
                return true;

            var found = false;
            foreach (var version in _versions)
            {
                if (version.IsImported && version.GameDirectory == directory)
                {
                    if (version.IsStateChanging)
                    {
                        _dispatcher.Invoke(() => MessageBox.Show("A version with the same name was already imported, and is currently being modified. Please wait a few moments and try again.", "Error"));
                        return false;
                    }
                    MessageBoxResult messageBoxResult = _dispatcher.Invoke(() => MessageBox.Show("A version with the same name was already imported. Do you want to delete it ?", "Delete Confirmation", MessageBoxButton.YesNo));
                    if (messageBoxResult == MessageBoxResult.Yes)
                    {
                        await Remove(version);
                        found = true;
                        break;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            if (!found)
            {
                _dispatcher.Invoke(() => MessageBox.Show("The destination path for importing already exists and doesn't contain a Minecraft installation known to the launcher. To avoid loss of data, importing was aborted. Please remove the files manually.", "Error"));
                return false;
            }

            return true;
        }

        private async Task ExtractImportedPackageAsync(string packagePath, string safeFileName, string directory)
        {
            var versionEntry = _versions.AddEntry(safeFileName, directory);
            versionEntry.StateChangeInfo = new VersionStateChangeInfo(VersionState.Extracting);
            await Task.Run(() =>
            {
                try
                {
                    ZipFile.ExtractToDirectory(packagePath, directory);
                    try
                    {
                        File.Delete(Path.Combine(directory, "AppxSignature.p7x"));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to delete AppxSignature.p7x: " + ex);
                    }
                }
                catch (InvalidDataException ex)
                {
                    Debug.WriteLine("Failed extracting appx " + packagePath + ": " + ex);
                    _dispatcher.Invoke(() => MessageBox.Show("Failed to import appx " + safeFileName + ". It may be corrupted or not an appx file.\n\nExtraction error: " + ex.Message, "Import failure"));
                    return;
                }
                finally
                {
                    versionEntry.StateChangeInfo = null;
                }
            });
        }

        public void InvokeLaunch(Version v)
        {
            if (_hasLaunchTask)
                return;
            _hasLaunchTask = true;
            Task.Run(() => LaunchVersionAsync(v));
        }

        private async Task LaunchVersionAsync(Version v)
        {
            try
            {
                if (v.IsGdkInstalled)
                {
                    await LaunchGdkAsync(v);
                    return;
                }

                await LaunchUwpAsync(v);
            }
            finally
            {
                _hasLaunchTask = false;
            }
        }

        private async Task LaunchGdkAsync(Version v)
        {
            try
            {
                await GdkInstaller.LaunchGdk(v.VersionType == VersionType.Preview);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GDK launch failed:\n" + ex);
                _dispatcher.Invoke(() => MessageBox.Show("GDK launch failed:\n" + ex));
            }
        }

        private async Task LaunchUwpAsync(Version v)
        {
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Registering);
            string gameDir = Path.GetFullPath(v.GameDirectory);
            try
            {
                await ReRegisterPackage(v.GamePackageFamily, gameDir);
            }
            catch (Exception e)
            {
                Debug.WriteLine("App re-register failed:\n" + e);
                _dispatcher.Invoke(() => MessageBox.Show("App re-register failed:\n" + e));
                v.StateChangeInfo = null;
                return;
            }

            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Launching);
            try
            {
                var pkg = await AppDiagnosticInfo.RequestInfoForPackageAsync(v.GamePackageFamily);
                if (pkg.Count > 0)
                    await pkg[0].LaunchAsync();
                Debug.WriteLine("App launch finished!");
                v.StateChangeInfo = null;
            }
            catch (Exception e)
            {
                Debug.WriteLine("App launch failed:\n" + e);
                _dispatcher.Invoke(() => MessageBox.Show("App launch failed:\n" + e));
                v.StateChangeInfo = null;
            }
        }

        private async Task DeploymentProgressWrapper(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> t)
        {
            TaskCompletionSource<int> src = new TaskCompletionSource<int>();
            t.Progress += (v, p) =>
            {
                Debug.WriteLine("Deployment progress: " + p.state + " " + p.percentage + "%");
            };
            t.Completed += (v, p) =>
            {
                if (p == AsyncStatus.Error)
                {
                    Debug.WriteLine("Deployment failed: " + v.GetResults().ErrorText);
                    src.SetException(new Exception("Deployment failed: " + v.GetResults().ErrorText));
                }
                else
                {
                    Debug.WriteLine("Deployment done: " + p);
                    src.SetResult(1);
                }
            };
            await src.Task;
        }

        private string GetBackupMinecraftDataDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tmpDir = Path.Combine(localAppData, "TmpMinecraftLocalState");
            return tmpDir;
        }

        private void BackupMinecraftDataForRemoval(string packageFamily)
        {
            var data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            string tmpDir = GetBackupMinecraftDataDir();
            if (Directory.Exists(tmpDir))
            {
                Debug.WriteLine("BackupMinecraftDataForRemoval error: " + tmpDir + " already exists");
                Process.Start("explorer.exe", tmpDir);
                _dispatcher.Invoke(() => MessageBox.Show("The temporary directory for backing up MC data already exists. This probably means that we failed last time backing up the data. Please back the directory up manually."));
                throw new Exception("Temporary dir exists");
            }
            Debug.WriteLine("Moving Minecraft data to: " + tmpDir);
            Directory.Move(data.LocalFolder.Path, tmpDir);
        }

        private void RestoreMove(string from, string to)
        {
            foreach (var f in Directory.EnumerateFiles(from))
            {
                string ft = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(ft))
                {
                    if (_dispatcher.Invoke(() => MessageBox.Show("The file " + ft + " already exists in the destination.\nDo you want to replace it? The old file will be lost otherwise.", "Restoring data directory from previous installation", MessageBoxButton.YesNo)) != MessageBoxResult.Yes)
                        continue;
                    File.Delete(ft);
                }
                File.Move(f, ft);
            }
            foreach (var f in Directory.EnumerateDirectories(from))
            {
                string tp = Path.Combine(to, Path.GetFileName(f));
                if (!Directory.Exists(tp))
                {
                    if (File.Exists(tp) && _dispatcher.Invoke(() => MessageBox.Show("The file " + tp + " is not a directory. Do you want to remove it? The data from the old directory will be lost otherwise.", "Restoring data directory from previous installation", MessageBoxButton.YesNo)) != MessageBoxResult.Yes)
                        continue;
                    Directory.CreateDirectory(tp);
                }
                RestoreMove(f, tp);
            }
        }

        private void RestoreMinecraftDataFromReinstall(string packageFamily)
        {
            string tmpDir = GetBackupMinecraftDataDir();
            if (!Directory.Exists(tmpDir))
                return;
            var data = ApplicationDataManager.CreateForPackageFamily(packageFamily);
            Debug.WriteLine("Moving backup Minecraft data to: " + data.LocalFolder.Path);
            RestoreMove(tmpDir, data.LocalFolder.Path);
            Directory.Delete(tmpDir, true);
        }

        private async Task RemovePackage(Package pkg, string packageFamily)
        {
            Debug.WriteLine("Removing package: " + pkg.Id.FullName);
            if (!pkg.IsDevelopmentMode)
            {
                BackupMinecraftDataForRemoval(packageFamily);
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, 0));
            }
            else
            {
                Debug.WriteLine("Package is in development mode");
                await DeploymentProgressWrapper(new PackageManager().RemovePackageAsync(pkg.Id.FullName, RemovalOptions.PreserveApplicationData));
            }
            Debug.WriteLine("Removal of package done: " + pkg.Id.FullName);
        }

        private string GetPackagePath(Package pkg)
        {
            try
            {
                return pkg.InstalledLocation.Path;
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
        }

        private async Task UnregisterPackage(string packageFamily, string gameDir)
        {
            foreach (var pkg in new PackageManager().FindPackages(packageFamily))
            {
                string location = GetPackagePath(pkg);
                if (location == string.Empty || location == gameDir)
                {
                    await RemovePackage(pkg, packageFamily);
                }
            }
        }

        private async Task ReRegisterPackage(string packageFamily, string gameDir)
        {
            foreach (var pkg in new PackageManager().FindPackages(packageFamily))
            {
                string location = GetPackagePath(pkg);
                if (location == gameDir)
                {
                    Debug.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + location);
                    return;
                }
                await RemovePackage(pkg, packageFamily);
            }
            Debug.WriteLine("Registering package");
            string manifestPath = Path.Combine(gameDir, "AppxManifest.xml");
            await DeploymentProgressWrapper(new PackageManager().RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode));
            Debug.WriteLine("App re-register done!");
            RestoreMinecraftDataFromReinstall(packageFamily);
        }

        public void InvokeDownload(Version v)
        {
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            v.IsNew = false;
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Initializing);
            v.StateChangeInfo.CancelCommand = new RelayCommand(o => cancelSource.Cancel());

            Debug.WriteLine("Download start");
            Task.Run(() => DownloadAndInstallAsync(v, cancelSource));
        }

        private async Task DownloadAndInstallAsync(Version v, CancellationTokenSource cancelSource)
        {
            string safeName = SanitizeFileName(v.Name);
            string dlPath = (v.VersionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + safeName + (v.VersionType == VersionType.Preview ? ".msixvc" : ".Appx");

            VersionDownloader downloader = await GetDownloaderOrNullAsync(v);
            if (downloader == null)
                return;

            if (await TryStreamingInstallForPreviewAsync(v, downloader, cancelSource.Token))
                return;

            if (!await TryDownloadToFileAsync(v, downloader, dlPath, cancelSource.Token))
                return;

            NormalizeDownloadedExtension(ref dlPath);

            if (!await TryExtractOrInstallAsync(v, dlPath, cancelSource.Token))
                return;

            v.StateChangeInfo = null;
            v.UpdateInstallStatus();
        }

        private async Task<VersionDownloader> GetDownloaderOrNullAsync(Version v)
        {
            VersionDownloader downloader = _anonVersionDownloader;
            if (v.VersionType == VersionType.Beta || v.VersionType == VersionType.Preview)
            {
                downloader = _userVersionDownloader;

                // Lazily initialize the login task on a background thread
                var existingTask = _userVersionDownloaderLoginTask;
                if (existingTask == null)
                {
                    var newTask = Task.Run(() =>
                    {
                        _userVersionDownloader.EnableUserAuthorization();
                    });

                    existingTask = Interlocked.CompareExchange(ref _userVersionDownloaderLoginTask, newTask, null) ?? newTask;
                }

                Debug.WriteLine("Waiting for authentication");
                try
                {
                    await existingTask;
                    Debug.WriteLine("Authentication complete");
                }
                catch (WUTokenHelper.WUTokenException e)
                {
                    Debug.WriteLine("Authentication failed:\n" + e);
                    _dispatcher.Invoke(() => MessageBox.Show("Failed to authenticate because: " + e.Message, "Authentication failed"));
                    v.StateChangeInfo = null;
                    return null;
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Authentication failed:\n" + e);
                    _dispatcher.Invoke(() => MessageBox.Show(e.ToString(), "Authentication failed"));
                    v.StateChangeInfo = null;
                    return null;
                }
            }

            return downloader;
        }

        private async Task<bool> TryStreamingInstallForPreviewAsync(Version v, VersionDownloader downloader, CancellationToken cancellationToken)
        {
            if (v.VersionType != VersionType.Preview)
                return false;

            try
            {
                string directUrl = await downloader.ResolveDownloadUrl(v.UUID, "1", cancellationToken);
                if (!string.IsNullOrEmpty(directUrl))
                {
                    Debug.WriteLine("Attempting GDK streaming install via wdapp from URL: " + directUrl);
                    v.StateChangeInfo.VersionState = VersionState.Extracting;
                    await GdkInstaller.InstallMsixvc(directUrl, true, cancellationToken);
                    v.IsGdkInstalled = true;
                    v.StateChangeInfo = null;
                    v.UpdateInstallStatus();
                    return true; // Success via streaming install; no local file download needed
                }
            }
            catch (Exception ge)
            {
                Debug.WriteLine("GDK streaming install failed; falling back to file download.\n" + ge);
                // Intentionally continue to download fallback
            }

            return false;
        }

        private async Task<bool> TryDownloadToFileAsync(Version v, VersionDownloader downloader, string dlPath, CancellationToken cancellationToken)
        {
            try
            {
                await downloader.Download(v.UUID, "1", dlPath, (current, total) =>
                {
                    if (v.StateChangeInfo.VersionState != VersionState.Downloading)
                    {
                        Debug.WriteLine("Actual download started");
                        v.StateChangeInfo.VersionState = VersionState.Downloading;
                        if (total.HasValue)
                            v.StateChangeInfo.TotalSize = total.Value;
                    }
                    v.StateChangeInfo.DownloadedBytes = current;
                }, cancellationToken);
                Debug.WriteLine("Download complete");
                return true;
            }
            catch (BadUpdateIdentityException)
            {
                Debug.WriteLine("Download failed due to failure to fetch download URL");
                _dispatcher.Invoke(() => MessageBox.Show(
                    "Unable to fetch download URL for version." +
                    (v.VersionType == VersionType.Beta ? "\nFor beta versions, please make sure your account is subscribed to the Minecraft beta programme in the Xbox Insider Hub app." : "")
                ));
                v.StateChangeInfo = null;
                return false;
            }
            catch (Exception e)
            {
                Debug.WriteLine("Download failed:\n" + e);
                if (!(e is TaskCanceledException))
                    _dispatcher.Invoke(() => MessageBox.Show("Download failed:\n" + e));
                v.StateChangeInfo = null;
                return false;
            }
        }

        private void NormalizeDownloadedExtension(ref string dlPath)
        {
            try
            {
                bool isZip = IsZipFile(dlPath);
                string desiredExt = isZip ? ".Appx" : ".msixvc";
                string currentExt = Path.GetExtension(dlPath);
                if (!string.Equals(currentExt, desiredExt, StringComparison.OrdinalIgnoreCase))
                {
                    string newPath = Path.ChangeExtension(dlPath, desiredExt);
                    try
                    {
                        if (File.Exists(newPath)) File.Delete(newPath);
                        File.Move(dlPath, newPath);
                        dlPath = newPath;
                    }
                    catch (Exception rex)
                    {
                        Debug.WriteLine("Failed to rename downloaded file: " + rex.Message);
                    }
                }
            }
            catch (Exception sigex)
            {
                Debug.WriteLine("Signature check failed: " + sigex.Message);
            }
        }

        private async Task<bool> TryExtractOrInstallAsync(Version v, string dlPath, CancellationToken cancellationToken)
        {
            try
            {
                v.StateChangeInfo.VersionState = VersionState.Extracting;
                string dirPath = v.GameDirectory;
                if (Directory.Exists(dirPath))
                    Directory.Delete(dirPath, true);
                try
                {
                    ZipFile.ExtractToDirectory(dlPath, dirPath);
                    v.StateChangeInfo = null;
                    try
                    {
                        File.Delete(Path.Combine(dirPath, "AppxSignature.p7x"));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to delete AppxSignature.p7x: " + ex);
                    }
                }
                catch (InvalidDataException)
                {
                    Debug.WriteLine("Not a ZIP/Appx - attempting GDK (MSIXVC) install via wdapp.exe");
                    try
                    {
                        await GdkInstaller.InstallMsixvc(dlPath, true, cancellationToken);
                        v.IsGdkInstalled = true;
                        v.StateChangeInfo = null;
                    }
                    catch (Exception ge)
                    {
                        Debug.WriteLine("GDK install failed:\n" + ge);
                        _dispatcher.Invoke(() => MessageBox.Show("GDK install failed:\n" + ge));
                        v.StateChangeInfo = null;
                        return false;
                    }
                }
                if (_userPrefs.DeleteAppxAfterDownload)
                {
                    try
                    {
                        Debug.WriteLine("Deleting downloaded package to reduce disk usage");
                        File.Delete(dlPath);
                    }
                    catch (Exception de)
                    {
                        Debug.WriteLine("Failed to delete downloaded package: " + de.Message);
                    }
                }
                else
                {
                    Debug.WriteLine("Not deleting downloaded package due to user preferences");
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.WriteLine("Extraction/Install step failed:\n" + e);
                _dispatcher.Invoke(() => MessageBox.Show("Extraction/Install step failed:\n" + e));
                v.StateChangeInfo = null;
                return false;
            }
        }

        public async Task Remove(Version v)
        {
            v.StateChangeInfo = new VersionStateChangeInfo(VersionState.Uninstalling);
            if (v.IsGdkInstalled)
            {
                try
                {
                    await GdkInstaller.UninstallGdk(v.VersionType == VersionType.Preview);
                    v.IsGdkInstalled = false;
                    _dispatcher.Invoke(() => MessageBox.Show("GDK uninstall completed."));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("GDK uninstall failed:\n" + ex);
                    _dispatcher.Invoke(() => MessageBox.Show("GDK uninstall failed:\n" + ex));
                }
            }
            else
            {
                await UnregisterPackage(v.GamePackageFamily, Path.GetFullPath(v.GameDirectory));
                if (Directory.Exists(v.GameDirectory))
                    Directory.Delete(v.GameDirectory, true);
            }
            v.StateChangeInfo = null;
            if (v.IsImported)
            {
                _dispatcher.Invoke(() => _versions.Remove(v));
                Debug.WriteLine("Removed imported version " + v.DisplayName);
            }
            else
            {
                v.UpdateInstallStatus();
                Debug.WriteLine("Removed release version " + v.DisplayName);
            }
        }

        public void InvokeRemove(Version v)
        {
            Task.Run(async () => await Remove(v));
        }

        private static bool IsZipFile(string path)
        {
            // ZIP signature: 50 4B 03 04
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 4) return false;
                    var sig = new byte[4];
                    int r = fs.Read(sig, 0, 4);
                    return r == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
                }
            }
            catch { return false; }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "file";
            return cleaned.Trim();
        }

        public void SignIn()
        {
            Task.Run(() =>
            {
                try
                {
                    _userVersionDownloader.EnableUserAuthorization();
                    _dispatcher.Invoke(() => MessageBox.Show("Signed in successfully. Tokens retrieved.", "Sign in", MessageBoxButton.OK, MessageBoxImage.Information));
                }
                catch (WUTokenHelper.WUTokenException ex)
                {
                    Debug.WriteLine("Sign in failed (WU token error):\n" + ex);
                    _dispatcher.Invoke(() => MessageBox.Show("Failed to fetch tokens: " + ex.Message, "Sign in failed", MessageBoxButton.OK, MessageBoxImage.Error));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Sign in failed:\n" + ex);
                    _dispatcher.Invoke(() => MessageBox.Show(ex.ToString(), "Sign in failed", MessageBoxButton.OK, MessageBoxImage.Error));
                }
            });
        }
    }
}
