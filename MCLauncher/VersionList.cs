using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MCLauncher {
    class VersionList : ObservableCollection<WPFDataTypes.Version> {

        private WPFDataTypes.ICommonVersionCommands _commands;
        private readonly HttpClient _client = new HttpClient();

        public VersionList(WPFDataTypes.ICommonVersionCommands commands) {
            _commands = commands;
        }

        public async Task DownloadList() {
            var resp = await _client.GetAsync("https://mrarm.io/u/versions-mcw10.json");
            resp.EnsureSuccessStatusCode();
            var data = JArray.Parse(await resp.Content.ReadAsStringAsync());
            // ([name, uuid, isBeta])[]
            foreach (JArray o in data.AsEnumerable().Reverse()) {
                Add(new WPFDataTypes.Version(o[1].Value<string>(), o[0].Value<string>(), o[2].Value<int>() == 1, _commands));
            }
        }

    }
}
