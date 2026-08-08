using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SecureFileTransferService
{
    public class SourceSettings
    {
        public string RootFolder { get; set; }
        public string ProcessedFolder { get; set; }
        public string ErrorFolder { get; set; }

        public Dictionary<string, string[]> FolderStructure { get; set; }
    }
}
