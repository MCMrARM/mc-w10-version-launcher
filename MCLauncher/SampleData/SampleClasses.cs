using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MCLauncher.SampleData
{
    public class SampleVersion
    {
        public string Name { get; set; }

        public string DisplayName { get; set; }
        public string DisplayInstallStatus { get; set; }

        public bool IsInstalled { get; set; }

        public bool IsBeta { get; set; }
    }

    public class SampleVersionList : List<SampleVersion> { }
}
