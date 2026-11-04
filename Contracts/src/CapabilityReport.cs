using System.Collections.Generic;

namespace Regions.Contracts
{
    /// <summary>
    /// What CS2Integration found about the running game and what it allowed itself to do (log §21.4, §23.4).
    /// Schema: schema/capability-report.v1.schema.json.
    /// </summary>
    public sealed class CapabilityReport
    {
        public int SchemaVersion { get; set; } = SchemaVersions.CapabilityReport;
        public string? GameBuild { get; set; }
        public string ModVersion { get; set; } = "";
        /// <summary>Sorted by Id. Empty when enumeration is not yet possible (V-26). See ActiveModsEnumerated.</summary>
        public List<ModInfo> ActiveMods { get; set; } = new List<ModInfo>();
        public bool ActiveModsEnumerated { get; set; }
        /// <summary>One entry per Feature member, sorted in enum order.</summary>
        public List<FeatureState> Features { get; set; } = new List<FeatureState>();
        /// <summary>Assumption ids (V-xx) verified on this build and relied on by enabled features.</summary>
        public List<string> VerifiedAssumptions { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class ModInfo
    {
        public string Id { get; set; } = "";
        public string? Version { get; set; }
    }

    public sealed class FeatureState
    {
        public Feature Feature { get; set; }
        public FeatureStatus Status { get; set; } = FeatureStatus.Disabled;
        public string Reason { get; set; } = "";
    }
}
