using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MCLauncher {
    public class VersionList : ObservableCollection<WPFDataTypes.Version> {

        public string VersionsApi { get; set; }

        private readonly string _cacheFile;
        private readonly string _importedDirectory;
        private readonly WPFDataTypes.ICommonVersionCommands _commands;
        private readonly HttpClient _client = new HttpClient();
        HashSet<string> dbVersions = new HashSet<string>();

        private PropertyChangedEventHandler _versionPropertyChangedHandler;
        public VersionList(string cacheFile, string importedDirectory, string versionsApi, WPFDataTypes.ICommonVersionCommands commands, PropertyChangedEventHandler versionPropertyChangedEventHandler) {
            _cacheFile = cacheFile;
            _importedDirectory = importedDirectory;
            VersionsApi = versionsApi;
            _commands = commands;
            _versionPropertyChangedHandler = versionPropertyChangedEventHandler;
            CollectionChanged += versionListOnCollectionChanged;
        }

        private void versionListOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
            if (e.OldItems != null) {
                foreach (var item in e.OldItems) {
                    var version = item as WPFDataTypes.Version;
                    version.PropertyChanged -= _versionPropertyChangedHandler;
                }
            }
            if (e.NewItems != null) {
                foreach (var item in e.NewItems) {
                    var version = item as WPFDataTypes.Version;
                    version.PropertyChanged += _versionPropertyChangedHandler;
                }
            }
        }

        private void ParseList(JArray data, bool isCache) {
            Clear();
            // ([name, uuid, versionType])[]
            foreach (JArray o in data.AsEnumerable().Reverse()) {
                bool isNew = dbVersions.Add(o[0].Value<string>()) && !isCache;
                int versionType = o[2].Value<int>();
                if (!Enum.IsDefined(typeof(WPFDataTypes.VersionType), versionType) || versionType == (int) WPFDataTypes.VersionType.Imported)
                    continue;
                Add(new WPFDataTypes.Version(o[1].Value<string>(), o[0].Value<string>(), (WPFDataTypes.VersionType) versionType, isNew, _commands));
            }
        }

        public async Task LoadImported() {
            string[] subdirectoryEntries = await Task.Run(() => Directory.Exists(_importedDirectory) ? Directory.GetDirectories(_importedDirectory) : Array.Empty<string>());
            foreach (string subdirectory in subdirectoryEntries) {
                AddEntry(Path.GetFileName(subdirectory), subdirectory);
            }
        }

        public async Task LoadFromCache() {
            try {
                using (var reader = File.OpenText(_cacheFile)) {
                    var data = await reader.ReadToEndAsync();
                    ParseList(JArray.Parse(data), true);
                }
            } catch (FileNotFoundException) { // ignore
            }
        }

        public async Task DownloadList() {
            var resp = await _client.GetAsync(VersionsApi);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadAsStringAsync();
            File.WriteAllText(_cacheFile, data);
            ParseList(JArray.Parse(data), false);
        }

        public WPFDataTypes.Version AddEntry(string name, string path) {
            var result = new WPFDataTypes.Version(name.Replace(".appx", ""), path, _commands);
            Add(result);
            return result;
        }

    }
}
