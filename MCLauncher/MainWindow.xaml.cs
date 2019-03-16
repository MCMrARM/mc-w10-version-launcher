using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MCLauncher {
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Threading;
    using WPFDataTypes;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {

        private List<Version> _versions;

        public MainWindow() {
            InitializeComponent();
            _versions = new List<Version>();
            RelayCommand downloadCommand = new RelayCommand(InvokeDownload);
            _versions.Add(new Version("f5c96a67-9beb-4291-8d56-3a872f363f68", "1.9.0.15", false, downloadCommand));
            _versions.Add(new Version("a0813887-d274-4742-9bc4-6dcca29abfeb", "1.10.0.5", true, downloadCommand));
            VersionList.ItemsSource = _versions;
        }

        private void InvokeDownload(object param) {
            Version v = (Version)param;
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            v.DownloadInfo = new VersionDownloadInfo();
            v.DownloadInfo.IsInitializing = true;
            v.DownloadInfo.CancelCommand = new RelayCommand((o) => cancelSource.Cancel());

            VersionDownloader downloader = new VersionDownloader();
            Debug.WriteLine("Download start");
            Task.Run(async () => {
                try {
                    await downloader.Download(v.UUID, "1", (current, total) => {
                        if (v.DownloadInfo.IsInitializing) {
                            Debug.WriteLine("Actual download started");
                            v.DownloadInfo.IsInitializing = false;
                            if (total.HasValue)
                                v.DownloadInfo.TotalSize = total.Value;
                        }
                        v.DownloadInfo.DownloadedBytes = current;
                    }, cancelSource.Token);
                    Debug.WriteLine("Download complete");
                    v.DownloadInfo = null;
                } catch (Exception e) {
                    Debug.WriteLine("Download failed:\n" + e.ToString());
                    if (!(e is TaskCanceledException))
                        MessageBox.Show("Download failed:\n" + e.ToString());
                    v.DownloadInfo = null;
                }
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

        public class Versions : List<Object> {
        }

        public class Version : NotifyPropertyChangedBase {

            public Version() { }
            public Version(string uuid, string name, bool isBeta, ICommand downloadCommand) {
                this.UUID = uuid;
                this.Name = name;
                this.IsBeta = isBeta;
                this.DownloadCommand = downloadCommand;
            }

            public string UUID { get; set; }
            public string Name { get; set; }
            public bool IsBeta { get; set; }

            public string DisplayName {
                get {
                    return Name + (IsBeta ? " (beta)" : "");
                }
            }
            public string DisplayInstallStatus {
                get {
                    return "Not installed";
                }
            }

            public ICommand DownloadCommand { get; set; }

            private VersionDownloadInfo _downloadInfo;
            public VersionDownloadInfo DownloadInfo {
                get { return _downloadInfo; }
                set { _downloadInfo = value; OnPropertyChanged("DownloadInfo"); OnPropertyChanged("IsDownloading"); }
            }

            public bool IsDownloading => DownloadInfo != null;

        }

        public class VersionDownloadInfo : NotifyPropertyChangedBase {

            private bool _isInitializing;
            private long _downloadedBytes;
            private long _totalSize;

            public bool IsInitializing {
                get { return _isInitializing; }
                set { _isInitializing = value; OnPropertyChanged("IsInitializing"); OnPropertyChanged("DisplayStatus"); }
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
                    return "Downloading... " + (DownloadedBytes / 1024 / 1024) + "MiB/" + (TotalSize / 1024 / 1024) + "MiB";
                }
            }

            public ICommand CancelCommand { get; set; }

        }

    }
}
