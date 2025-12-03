using Newtonsoft.Json;

namespace MCLauncher {
    public class Preferences {
        public bool ShowInstalledOnly { get; set; } = false;

        public bool DeleteAppxAfterDownload { get; set; } = true;

        [JsonProperty("VersionsApi")]
        public string VersionsApiUWP { get; set; } = "";

        public string VersionsApiGDK { get; set; } = "";

        public bool HasPreviouslyUsedGDK { get; set; } = false;
    }
}
