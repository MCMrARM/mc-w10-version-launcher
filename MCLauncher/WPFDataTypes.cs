using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace MCLauncher.WPFDataTypes
{
    public class NotifyPropertyChangedBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }

    public interface ICommonVersionCommands
    {
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

    public class Version : NotifyPropertyChangedBase
    {
        public static readonly string UNKNOWN_UUID = "UNKNOWN";

        public Version(string uuid, string name, VersionType versionType, bool isNew, ICommonVersionCommands commands)
        {
            UUID = uuid;
            Name = name;
            VersionType = versionType;
            IsNew = isNew;
            DownloadCommand = commands.DownloadCommand;
            LaunchCommand = commands.LaunchCommand;
            RemoveCommand = commands.RemoveCommand;
            GameDirectory = (versionType == VersionType.Preview ? "Minecraft-Preview-" : "Minecraft-") + Name;
        }

        public Version(string name, string directory, ICommonVersionCommands commands)
        {
            UUID = UNKNOWN_UUID;
            Name = name;
            VersionType = VersionType.Imported;
            DownloadCommand = commands.DownloadCommand;
            LaunchCommand = commands.LaunchCommand;
            RemoveCommand = commands.RemoveCommand;
            GameDirectory = directory;
        }

        public string UUID { get; set; }
        public string Name { get; set; }
        public VersionType VersionType { get; set; }

        private bool _isNew;
        public bool IsNew
        {
            get { return _isNew; }
            set
            {
                _isNew = value;
                OnPropertyChanged("IsNew");
            }
        }

        public bool IsImported => VersionType == VersionType.Imported;

        public string GameDirectory { get; set; }

        private bool _isGdkInstalled;
        public bool IsGdkInstalled
        {
            get => _isGdkInstalled;
            set { _isGdkInstalled = value; OnPropertyChanged("IsGdkInstalled"); OnPropertyChanged("IsInstalled"); }
        }

        public string GamePackageFamily
        {
            get => VersionType == VersionType.Preview ? MinecraftPackageFamilies.MINECRAFT_PREVIEW : MinecraftPackageFamilies.MINECRAFT;
        }

        public bool IsInstalled => Directory.Exists(GameDirectory) || IsGdkInstalled;

        public string DisplayName
        {
            get
            {
                string typeTag = "";
                if (VersionType == VersionType.Beta)
                    typeTag = "(beta)";
                else if (VersionType == VersionType.Preview)
                    typeTag = "(preview)";
                return Name + (typeTag.Length > 0 ? " " + typeTag : "") + (IsNew ? " (NEW!)" : "");
            }
        }

        public string DisplayInstallStatus
        {
            get
            {
                if (!IsInstalled)
                    return "Not installed (click Download to install)";

                if (IsGdkInstalled)
                    return "Installed via GDK";

                if (Directory.Exists(GameDirectory))
                    return "Installed (local folder)";

                return "Installed";
            }
        }

        public ICommand LaunchCommand { get; set; }
        public ICommand DownloadCommand { get; set; }
        public ICommand RemoveCommand { get; set; }

        private VersionStateChangeInfo _stateChangeInfo;

        public VersionStateChangeInfo StateChangeInfo
        {
            get { return _stateChangeInfo; }
            set { _stateChangeInfo = value; OnPropertyChanged("StateChangeInfo"); OnPropertyChanged("IsStateChanging"); }
        }

        public bool IsStateChanging => StateChangeInfo != null;

        public void UpdateInstallStatus()
        {
            OnPropertyChanged("IsInstalled");
        }
    }

    public enum VersionState
    {
        Initializing,
        Downloading,
        Extracting,
        Registering,
        Launching,
        Uninstalling
    }

    public class VersionStateChangeInfo : NotifyPropertyChangedBase
    {
        private VersionState _versionState;
        private long _downloadedBytes;
        private long _totalSize;

        public VersionStateChangeInfo(VersionState versionState)
        {
            _versionState = versionState;
        }

        public VersionState VersionState
        {
            get { return _versionState; }
            set
            {
                _versionState = value;
                OnPropertyChanged("IsProgressIndeterminate");
                OnPropertyChanged("DisplayStatus");
            }
        }

        public bool IsProgressIndeterminate
        {
            get
            {
                switch (_versionState)
                {
                    case VersionState.Initializing:
                    case VersionState.Extracting:
                    case VersionState.Uninstalling:
                    case VersionState.Registering:
                    case VersionState.Launching:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public long DownloadedBytes
        {
            get { return _downloadedBytes; }
            set { _downloadedBytes = value; OnPropertyChanged("DownloadedBytes"); OnPropertyChanged("DisplayStatus"); }
        }

        public long TotalSize
        {
            get { return _totalSize; }
            set { _totalSize = value; OnPropertyChanged("TotalSize"); OnPropertyChanged("DisplayStatus"); }
        }

        public string DisplayStatus
        {
            get
            {
                switch (_versionState)
                {
                    case VersionState.Initializing: return "Preparing...";
                    case VersionState.Downloading:
                        return "Downloading... " + (DownloadedBytes / 1024 / 1024) + "MiB/" + (TotalSize / 1024 / 1024) + "MiB";
                    case VersionState.Extracting: return "Extracting...";
                    case VersionState.Registering: return "Registering package...";
                    case VersionState.Launching: return "Launching...";
                    case VersionState.Uninstalling: return "Uninstalling...";
                    default: return "Unknown state...";
                }
            }
        }

        public ICommand CancelCommand { get; set; }
    }
}
