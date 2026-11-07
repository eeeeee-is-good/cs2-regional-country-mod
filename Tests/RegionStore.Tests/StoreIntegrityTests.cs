using System.Text;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Regions.Simulator;
using Regions.Store;
using Xunit;
using ContractJson = Regions.Store.ContractJson;

namespace Regions.StoreTests;

/// <summary>Store creation, manifest concurrency, corruption detection and crash recovery.</summary>
public sealed class StoreIntegrityTests : IDisposable
{
    private readonly StoreFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Create_makes_the_documented_layout()
    {
        Assert.True(File.Exists(_f.Store.ManifestPath));
        Assert.True(File.Exists(_f.Store.IndexPath));
        Assert.True(Directory.Exists(_f.Store.InboxDirectory));
        var index = _f.Store.ReadIndex();
        Assert.Equal((0, 0), (index.CurrentRegionSeq, index.Snapshots.Count));
    }

    [Fact]
    public void Create_refuses_a_non_empty_folder()
    {
        var manifest = _f.Store.ReadManifest();
        var before = _f.Snapshot();
        Assert.Throws<StoreException>(() => FileRegionStore.Create(_f.Root, manifest));
        Assert.Equal(before, _f.Snapshot());
    }

    [Fact]
    public void Open_needs_a_region_folder()
    {
        Assert.Throws<StoreException>(() => FileRegionStore.Open(Path.Combine(_f.Root, "nope")));
        Assert.Throws<StoreException>(() => FileRegionStore.Open(_f.Store.InboxDirectory));
    }

    [Fact]
    public void Golden_fixture_opens_read_only()
    {
        var golden = Repo.Golden("v1-basic");
        var before = Directory.GetFiles(golden, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => ContractFiles.Sha256(File.ReadAllBytes(p)));
        var latest = FileRegionStore.Open(golden).LatestSnapshots();
        Assert.Equal(new[] { "city-a", "city-b", "city-c" }, latest.Select(p => p.Snapshot.CityId));
        Assert.Equal(new[] { 1, 2, 3 }, latest.Select(p => p.RegionSeq));
        Assert.Equal(before, Directory.GetFiles(golden, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => ContractFiles.Sha256(File.ReadAllBytes(p))));
    }

    // ---- Manifest ----

    [Fact]
    public void Manifest_write_bumps_revision()
    {
        var m = _f.Store.ReadManifest();
        Assert.Equal(1, m.Revision);
        m.Links[0].DistanceKm = 123;
        _f.Store.WriteManifest(m, expectedRevision: 1);
        var reread = _f.Store.ReadManifest();
        Assert.Equal(2, reread.Revision);
        Assert.Equal(123, reread.Links[0].DistanceKm);
    }

    [Fact]
    public void Stale_manifest_write_is_refused()
    {
        var mine = _f.Store.ReadManifest();
        var theirs = _f.Store.ReadManifest();
        theirs.Links.RemoveAt(0);
        _f.Store.WriteManifest(theirs, 1);
        var before = File.ReadAllBytes(_f.Store.ManifestPath);

        mine.Links[0].DistanceKm = 1;
        Assert.Throws<ManifestConflictException>(() => _f.Store.WriteManifest(mine, 1));
        Assert.Equal(before, File.ReadAllBytes(_f.Store.ManifestPath));
    }

    [Fact]
    public void Invalid_manifest_is_never_written()
    {
        var m = _f.Store.ReadManifest();
        var before = File.ReadAllBytes(_f.Store.ManifestPath);
        m.Links[0].DistanceKm = -5;
        Assert.Throws<ContractFileException>(() => _f.Store.WriteManifest(m, 1));
        Assert.Equal(before, File.ReadAllBytes(_f.Store.ManifestPath));
    }

    [Fact]
    public void Region_id_cannot_change()
    {
        var m = _f.Store.ReadManifest();
        m.RegionId = "another-region";
        Assert.Throws<StoreException>(() => _f.Store.WriteManifest(m, 1));
    }

    // ---- Corruption ----

    [Fact]
    public void Edited_accepted_snapshot_is_detected()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        _f.Store.IngestInbox();
        var path = _f.Store.SnapshotPath("ironvale", 1);
        File.WriteAllText(path, File.ReadAllText(path).Replace("1000", "9000"));
        var ex = Assert.Throws<StoreCorruptException>(() => _f.Store.LatestSnapshots());
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void Line_ending_conversion_is_detected()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        _f.Store.IngestInbox();
        var path = _f.Store.SnapshotPath("ironvale", 1);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));
        Assert.Throws<StoreCorruptException>(() => _f.Store.LatestSnapshots());
    }

    [Fact]
    public void Missing_accepted_snapshot_is_detected()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        _f.Store.IngestInbox();
        File.Delete(_f.Store.SnapshotPath("ironvale", 1));
        Assert.Throws<StoreCorruptException>(() => _f.Store.LatestSnapshots());
    }

    [Fact]
    public void Corrupt_index_is_detected()
    {
        File.WriteAllText(_f.Store.IndexPath, "{");
        Assert.Throws<ContractFileException>(() => _f.Store.ReadIndex());
        Assert.Throws<ContractFileException>(() => _f.Store.IngestInbox());
    }

    // ---- Crash recovery ----

    [Fact]
    public void Crash_after_copying_snapshot_but_before_index_recovers()
    {
        var s = Scenarios.Ironvale().Capture();
        var bytes = ContractJson.ToBytes(s);
        _f.Drop(s);
        // Simulate the crash: the immutable copy exists, the index was never updated.
        Directory.CreateDirectory(Path.GetDirectoryName(_f.Store.SnapshotPath("ironvale", 1))!);
        File.WriteAllBytes(_f.Store.SnapshotPath("ironvale", 1), bytes);
        File.WriteAllBytes(_f.Store.IndexPath + ".tmp", Encoding.UTF8.GetBytes("half-written"));

        var o = Assert.Single(_f.Store.IngestInbox().Outcomes);
        Assert.Equal(IngestStatus.Accepted, o.Status);
        Assert.Equal(1, _f.Store.ReadIndex().CurrentRegionSeq);
        Assert.False(File.Exists(_f.Store.IndexPath + ".tmp"));
    }

    [Fact]
    public void Unindexed_snapshot_with_different_bytes_is_never_overwritten()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        Directory.CreateDirectory(Path.GetDirectoryName(_f.Store.SnapshotPath("ironvale", 1))!);
        File.WriteAllText(_f.Store.SnapshotPath("ironvale", 1), "something else");
        var indexBefore = _f.IndexBytes();

        Assert.Throws<StoreCorruptException>(() => _f.Store.IngestInbox());
        Assert.Equal("something else", File.ReadAllText(_f.Store.SnapshotPath("ironvale", 1)));
        Assert.Equal(indexBefore, _f.IndexBytes());
    }

    // ---- Serializer (Contracts/README.md rules 1-3) ----

    [Fact]
    public void Contract_json_is_utf8_without_bom_lf_with_explicit_nulls()
    {
        var bytes = ContractJson.ToBytes(Scenarios.Carford().Capture());
        Assert.False(bytes[0] == 0xEF);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"capacityEstimatePerGameDay\": null", text);
        Assert.Contains("\"confidence\": \"Provisional\"", text);
        Assert.Equal(bytes, ContractJson.ToBytes(ContractJson.FromBytes<CitySnapshot>(bytes)));
    }
}
