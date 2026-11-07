using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;
using Regions.Store;

namespace Regions.ScenarioRunner;

/// <summary>CLI glue over RegionStore: load a region folder, write a synthetic one, write quote files.</summary>
public static class RegionFolder
{
    public static RegionState Load(string folder, string? resourceTable = null) =>
        FileRegionStore.Open(folder).LoadState(ResourceTable.Load(resourceTable));

    /// <summary>
    /// Writes a synthetic region as a new store, publishing each city through the real path: create the inbox file,
    /// then ingest it, in the scenario's RegionSeq order. Refuses an existing non-empty folder.
    /// </summary>
    public static void Write(RegionScenario scenario, string folder)
    {
        scenario.SortManifest();
        var store = FileRegionStore.Create(folder, scenario.Manifest);
        foreach (var pub in scenario.LatestSnapshots.OrderBy(p => p.RegionSeq))
        {
            var s = pub.Snapshot;
            var bytes = ContractFiles.Serialize(s, ContractFiles.CitySnapshotSchema, $"{s.CityId} seq {s.Seq}");
            using (var file = new FileStream(store.InboxPath(s.CityId, s.Seq), FileMode.CreateNew, FileAccess.Write))
                file.Write(bytes);
            var result = store.IngestInbox();
            var mine = result.Outcomes.Single(o => o.CityId == s.CityId && o.Seq == s.Seq);
            if (mine.Status != IngestStatus.Accepted)
                throw new StoreException($"scenario {scenario.Name} cannot be stored as written: {mine}");
        }
    }

    public static void WriteQuote(QuoteResult quote, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"quote.{quote.DestCityId}.{quote.ResourceId}.json");
        File.WriteAllBytes(path, ContractFiles.Serialize(quote, ContractFiles.QuoteSchema, path));
    }
}
