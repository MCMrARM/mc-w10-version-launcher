using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MCLauncher {
    class VersionList : ObservableCollection<WPFDataTypes.Version> {

        private readonly string _cacheFile;
        private readonly string _importedDirectory;
        private readonly WPFDataTypes.ICommonVersionCommands _commands;
        private readonly HttpClient _client = new HttpClient();
        HashSet<string> dbVersions = new HashSet<string>();

        public VersionList(string cacheFile, string importedDirectory, WPFDataTypes.ICommonVersionCommands commands) {
            _cacheFile = cacheFile;
            _importedDirectory = importedDirectory;
            _commands = commands;
        }

        private void ParseList(JArray data) {
            Clear();
            // ([name, uuid, isBeta])[]
            foreach (JArray o in data.AsEnumerable().Reverse()) {
                dbVersions.Add(o[0].Value<string>());
                Add(new WPFDataTypes.Version(o[1].Value<string>(), o[0].Value<string>(), o[2].Value<int>() == 1, _commands));
            }
        }

        public async Task LoadImported() {
            string[] subdirectoryEntries = await Task.Run(() => Directory.GetDirectories(_importedDirectory));
            foreach (string subdirectory in subdirectoryEntries) {
                AddEntry(Path.GetFileName(subdirectory), subdirectory);
            }
        }

        public async Task LoadFromCache() {
            try {
                using (var reader = File.OpenText(_cacheFile)) {
                    var data = await reader.ReadToEndAsync();
                    ParseList(JArray.Parse(data));
                }
            } catch (FileNotFoundException) { // ignore
            }
        }

        public async Task DownloadList() {
            var resp = await _client.GetAsync("https://mrarm.io/r/w10-vdb");
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadAsStringAsync();
            File.WriteAllText(_cacheFile, data);
            ParseList(JArray.Parse(data));
        }

        public WPFDataTypes.Version AddEntry(string name, string path) {
            var result = new WPFDataTypes.Version(WPFDataTypes.Version.UNKNOWN_UUID, name.Replace(".appx", ""), false, path, _commands);
            Add(result);
            return result;
        }

    }
}
