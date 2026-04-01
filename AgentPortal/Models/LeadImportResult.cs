using System.Collections.Generic;

namespace AgentPortal.Models
{
    public sealed class LeadImportResult
    {
        public int Imported { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new();

        public int Processed => Imported + Updated + Skipped;
    }
}