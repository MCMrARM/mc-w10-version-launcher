using Newtonsoft.Json;
using System;
using System.Windows;
using System.Windows.Input;

namespace MCLauncher {
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Windows.Data;
    using WPFDataTypes;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {

        private static readonly string PREFS_PATH = @"preferences.json";
        private static readonly string IMPORTED_VERSIONS_PATH = @"imported_versions";
        private static readonly string VERSIONS_API = "https://mrarm.io/r/w10-vdb";

        private VersionList _versions;
        public Preferences UserPrefs { get; }

        private HashSet<CollectionViewSource> _versionListViews = new HashSet<CollectionViewSource>();

        private readonly VersionManager _versionManager;
        private readonly MainWindowViewModel _viewModel;
        private bool _isLoadingVersionList;

        public MainWindow() {
            if (File.Exists(PREFS_PATH)) {
                try {
                    var json = File.ReadAllText(PREFS_PATH);
                    var prefs = JsonConvert.DeserializeObject<Preferences>(json);
                    UserPrefs = prefs ?? new Preferences();
                } catch (Exception ex) {
                    Debug.WriteLine("Failed to load preferences, using defaults instead: " + ex);
                    UserPrefs = new Preferences();
                    try {
                        File.Move(PREFS_PATH, PREFS_PATH + ".bak");
                    } catch (Exception backupEx) {
                        Debug.WriteLine("Failed to backup corrupted preferences file: " + backupEx);
                    }
                    RewritePrefs();
                }
            } else {
                UserPrefs = new Preferences();
                RewritePrefs();
            }

            var versionsApi = UserPrefs.VersionsApi != "" ? UserPrefs.VersionsApi : VERSIONS_API;
            _versions = new VersionList("versions.json", IMPORTED_VERSIONS_PATH, versionsApi, null, VersionEntryPropertyChanged);
            _versionManager = new VersionManager(_versions, UserPrefs, Dispatcher, IMPORTED_VERSIONS_PATH);
            _viewModel = new MainWindowViewModel(UserPrefs, _versions, _versionManager, () => Dispatcher.Invoke(LoadVersionList));

            _versions.SetCommands(_viewModel);

            _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

            InitializeComponent();
            DataContext = _viewModel;

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

            Dispatcher.Invoke(LoadVersionList);
        }

        private async void LoadVersionList() {
            if (_isLoadingVersionList)
                return;

            _isLoadingVersionList = true;
            _viewModel.IsLoading = true;

            try {
                _viewModel.LoadingProgressText = "Loading versions from cache";
                _viewModel.LoadingProgressValue = 1;

                try {
                    await _versions.LoadFromCache();
                } catch (Exception e) {
                    Debug.WriteLine("List cache load failed:\n" + e.ToString());
                }

                _viewModel.LoadingProgressText = "Updating versions list from " + _versions.VersionsApi;
                _viewModel.LoadingProgressValue = 2;
                try {
                    await _versions.DownloadList();
                } catch (Exception e) {
                    Debug.WriteLine("List download failed:\n" + e.ToString());
                    MessageBox.Show("Failed to update version list from the internet. Some new versions might be missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                _viewModel.LoadingProgressText = "Loading imported versions";
                _viewModel.LoadingProgressValue = 3;
                await _versions.LoadImported();
                try {
                    await _versionManager.DetectPreviewGdkInstallAsync();
                } catch (Exception ex) {
                    Debug.WriteLine("DetectPreviewGdkInstall failed: " + ex.Message);
                }
            } finally {
                _viewModel.IsLoading = false;
                _isLoadingVersionList = false;
            }
        }

        private void VersionEntryPropertyChanged(object sender, PropertyChangedEventArgs e) {
            RefreshLists();
        }

        private void ViewModelOnPropertyChanged(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(MainWindowViewModel.ShowInstalledOnly)) {
                RefreshLists();
                RewritePrefs();
            } else if (e.PropertyName == nameof(MainWindowViewModel.DeleteAppxAfterDownload)) {
                RewritePrefs();
            } else if (e.PropertyName == nameof(MainWindowViewModel.VersionsApi)) {
                _versions.VersionsApi = string.IsNullOrEmpty(_viewModel.VersionsApi) ? VERSIONS_API : _viewModel.VersionsApi;
                Dispatcher.Invoke(LoadVersionList);
                RewritePrefs();
            }
        }

        private void RefreshLists() {
            Dispatcher.Invoke(() => {
                foreach (var list in _versionListViews) {
                    list.View.Refresh();
                }
            });
        }

        private void RewritePrefs() {
            File.WriteAllText(PREFS_PATH, JsonConvert.SerializeObject(UserPrefs));
        }

        private void onEndpointChangedHandler(object sender, string newEndpoint) {
            _viewModel.VersionsApi = newEndpoint;
        }

        private void MenuItemSetVersionListEndpointClicked(object sender, RoutedEventArgs e) {
            var dialog = new VersionListEndpointDialog(UserPrefs.VersionsApi) {
                Owner = this
            };
            dialog.OnEndpointChanged += onEndpointChangedHandler;

            dialog.Show();
        }
    }

}
