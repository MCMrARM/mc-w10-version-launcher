using MCLauncher.WPFDataTypes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MCLauncher {
    public class VersionList : ObservableCollection<WPFDataTypes.Version> {

        public string VersionsApiUWP { get; set; }
        public string VersionsApiGDK { get; set; }

        private static readonly string GDK_CONFIG_FILENAME = "MicrosoftGame.Config";

        private readonly string _cacheFileUWP;
        private readonly string _cacheFileGDK;
        private readonly string _importedDirectory;
        private readonly WPFDataTypes.ICommonVersionCommands _commands;
        private readonly HttpClient _client = new HttpClient();
        HashSet<string> dbVersions = new HashSet<string>();

        private PropertyChangedEventHandler _versionPropertyChangedHandler;
        public VersionList(string cacheFileUWP, string importedDirectory, string versionsApiUWP, WPFDataTypes.ICommonVersionCommands commands, PropertyChangedEventHandler versionPropertyChangedEventHandler, string cacheFileGDK, string versionsApiGDK) {
            _cacheFileUWP = cacheFileUWP;
            _cacheFileGDK = cacheFileGDK;
            _importedDirectory = importedDirectory;
            VersionsApiUWP = versionsApiUWP;
            VersionsApiGDK = versionsApiGDK;
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

        private void ParseListUWP(JArray data, bool isCache) {
            // ([name, uuid, versionType])[]
            foreach (JArray o in data.AsEnumerable().Reverse()) {
                bool exists = !dbVersions.Add(o[0].Value<string>());
                bool isNew = !exists && !isCache;
                int versionType = o[2].Value<int>();
                if (!Enum.IsDefined(typeof(WPFDataTypes.VersionType), versionType) || versionType == (int) WPFDataTypes.VersionType.Imported)
                    continue;
                Add(new WPFDataTypes.Version(o[1].Value<string>(), o[0].Value<string>(), (WPFDataTypes.VersionType)versionType, isNew, _commands, PackageType.UWP, null));
            }
        }

        private void ParseListGDK(JObject data, bool isCache, WPFDataTypes.VersionType versionType) {
            foreach (var keyValue in data.Properties().Reverse()) {
                string versionName = keyValue.Name;
                JArray urls = (JArray)keyValue.Value;

                List<string> downloadUrls = new List<string>();
                foreach (var url in urls) {
                    downloadUrls.Add(url.Value<string>());
                }
                if (downloadUrls.Count == 0) {
                    Debug.WriteLine("Not showing version " + versionName + " because it has no download URLs");
                    continue;
                }
                bool exists = !dbVersions.Add(versionName);
                bool isNew = !exists && !isCache;

                Add(new WPFDataTypes.Version(WPFDataTypes.Version.UNKNOWN_UUID, versionName, versionType, isNew, _commands, PackageType.GDK, downloadUrls));
            }
        }

        private void ParseDataGDK(JObject data, bool isCache) {
            ParseListGDK(data["release"] as JObject, isCache, WPFDataTypes.VersionType.Release);
            ParseListGDK(data["preview"] as JObject, isCache, WPFDataTypes.VersionType.Preview);
        }

        public void PrepareForReload() {
            for (int i = Count - 1; i >= 0; i--) {
                if (this[i].VersionType != WPFDataTypes.VersionType.Imported) {
                    RemoveAt(i);
                }
            }
        }

        public async Task LoadImported() {
            string[] subdirectoryEntries = await Task.Run(() => Directory.Exists(_importedDirectory) ? Directory.GetDirectories(_importedDirectory) : Array.Empty<string>());
            foreach (string subdirectory in subdirectoryEntries) {
                if (dbVersions.Add("IMPORTED_" + Path.GetFileName(subdirectory))) {
                    AddEntry(Path.GetFileName(subdirectory), subdirectory, File.Exists(Path.Combine(subdirectory, GDK_CONFIG_FILENAME)) ? PackageType.GDK : PackageType.UWP);
                }
            }
        }

        public async Task LoadFromCacheUWP() {
            try {
                using (var reader = File.OpenText(_cacheFileUWP)) {
                    var data = await reader.ReadToEndAsync();
                    ParseListUWP(JArray.Parse(data), true);
                }
            } catch (FileNotFoundException) { // ignore
            }
        }

        public async Task LoadFromCacheGDK() {
            try {
                using (var reader = File.OpenText(_cacheFileGDK)) {
                    var data = await reader.ReadToEndAsync();
                    ParseDataGDK(JObject.Parse(data), true);
                }
            } catch (FileNotFoundException) { // ignore
            }
        }

        public async Task DownloadVersionsGDK() {
            var resp = await _client.GetAsync(VersionsApiGDK);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadAsStringAsync();
            File.WriteAllText(_cacheFileGDK, data);
            ParseDataGDK(JObject.Parse(data), false);

        }

        public async Task DownloadVersionsUWP() { 
            var resp = await _client.GetAsync(VersionsApiUWP);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadAsStringAsync();
            File.WriteAllText(_cacheFileUWP, data);
            ParseListUWP(JArray.Parse(data), false);
        }

        public WPFDataTypes.Version AddEntry(string name, string path, PackageType packageType) {
            var result = new WPFDataTypes.Version(name.Replace(".appx", ""), path, _commands, packageType);
            Add(result);
            return result;
        }

    }
}
