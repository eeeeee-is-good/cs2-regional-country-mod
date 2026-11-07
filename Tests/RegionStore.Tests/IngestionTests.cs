using System.Text;
using System.Text.Json.Nodes;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Regions.Engine;
using Regions.Simulator;
using Regions.Store;
using Xunit;
using ContractJson = Regions.Store.ContractJson;

namespace Regions.StoreTests;

/// <summary>Contracts/README.md "Ingestion rules": accepted, duplicate, conflicts and rejected files.</summary>
public sealed class IngestionTests : IDisposable
{
    private readonly StoreFixture _f = new();
    public void Dispose() => _f.Dispose();

    private IngestOutcome IngestSingle()
    {
        var result = _f.Store.IngestInbox();
        return Assert.Single(result.Outcomes);
    }

    private void AssertNothingChanged(Dictionary<string, string> before) => Assert.Equal(before, _f.Snapshot());

    // ---- Accepted ----

    [Fact]
    public void First_snapshot_is_accepted_byte_for_byte()
    {
        var path = _f.Drop(Scenarios.Ironvale().Capture());
        var bytes = File.ReadAllBytes(path);

        var o = IngestSingle();
        Assert.Equal((IngestStatus.Accepted, IngestReason.Accepted, 1), (o.Status, o.Reason, o.RegionSeq));

        Assert.Equal(bytes, File.ReadAllBytes(_f.Store.SnapshotPath("ironvale", 1)));
        var index = _f.Store.ReadIndex();
        Assert.Equal(1, index.CurrentRegionSeq);
        var entry = Assert.Single(index.Snapshots);
        Assert.Equal(("ironvale", 1, 1, ContractFiles.Sha256(bytes)), (entry.CityId, entry.Seq, entry.RegionSeq, entry.FileSha256));
    }

    [Fact]
    public void Successive_publications_are_accepted_in_order()
    {
        var city = Scenarios.Ironvale();
        _f.Drop(city.Capture());
        _f.Drop(city.Advance(7).Capture());
        _f.Drop(city.Advance(7).Capture());
        var result = _f.Store.IngestInbox();
        Assert.Equal(new[] { 1, 2, 3 }, result.Outcomes.Select(o => o.Seq!.Value));
        Assert.All(result.Outcomes, o => Assert.Equal(IngestStatus.Accepted, o.Status));
        Assert.Equal(3, result.CurrentRegionSeq);
        Assert.Equal(3, _f.Store.LatestSnapshots().Single().Snapshot.Seq);
    }

    [Fact]
    public void Index_is_schema_valid_and_sorted()
    {
        var a = Scenarios.Ironvale();
        var b = Scenarios.Carford();
        _f.Drop(b.Capture()); _f.Drop(a.Capture());
        _f.Store.IngestInbox();
        _f.Drop(a.Capture()); _f.Drop(b.Capture());
        _f.Store.IngestInbox();

        Schemas.AssertValid("store-index.v1.schema.json", JsonNode.Parse(_f.IndexBytes()), "index");
        Assert.Equal(new[] { ("carford", 1), ("carford", 2), ("ironvale", 1), ("ironvale", 2) },
                     _f.Store.ReadIndex().Snapshots.Select(s => (s.CityId, s.Seq)));
        Assert.DoesNotContain((byte)'\r', _f.IndexBytes());
    }

    [Fact]
    public void Ingestion_order_does_not_depend_on_file_creation_order()
    {
        using var other = new StoreFixture();
        var a1 = Scenarios.Ironvale().Capture();
        var b1 = Scenarios.Carford().Capture();

        _f.Drop(a1); _f.Drop(b1);
        other.Drop(b1); other.Drop(a1);
        _f.Store.IngestInbox();
        other.Store.IngestInbox();
        Assert.Equal(_f.IndexBytes(), other.IndexBytes());
    }

    [Fact]
    public void Inbox_files_are_never_modified_and_no_temp_files_remain()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        _f.DropBytes("garbage.1.json", Encoding.UTF8.GetBytes("{ not json"));
        var inboxBefore = Directory.GetFiles(_f.Store.InboxDirectory).ToDictionary(p => p, File.ReadAllBytes);

        _f.Store.IngestInbox();

        Assert.Equal(inboxBefore.Keys.Order(), Directory.GetFiles(_f.Store.InboxDirectory).Order());
        foreach (var (p, bytes) in inboxBefore) Assert.Equal(bytes, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(_f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    // ---- Duplicate (G06) ----

    [Fact]
    public void G06_same_snapshot_ten_times_is_a_no_op()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        _f.Store.IngestInbox();
        var before = _f.Snapshot();

        for (var i = 0; i < 10; i++)
        {
            var o = IngestSingle();
            Assert.Equal((IngestStatus.Duplicate, IngestReason.AlreadyIngested, 1), (o.Status, o.Reason, o.RegionSeq));
        }
        AssertNothingChanged(before);
        Assert.Equal(1, _f.Store.ReadIndex().CurrentRegionSeq);
    }

    [Fact]
    public void G06_republished_identical_content_does_not_change_the_quote()
    {
        var a = Scenarios.Ironvale();
        _f.Drop(a.Capture());
        _f.Drop(Scenarios.Carford().Capture());
        _f.Store.IngestInbox();
        var engine = new LandedCostEngine();
        var request = new QuoteRequest { DestCityId = "carford", ResourceId = "Steel" };
        var before = engine.Quote(_f.Store.LoadState(ResourceTable.Load()), request).Offers.Select(o => o.LandedPrice).ToList();

        for (var i = 0; i < 10; i++) { _f.Drop(a.Capture()); _f.Store.IngestInbox(); }

        var after = engine.Quote(_f.Store.LoadState(ResourceTable.Load()), request);
        Assert.Equal(before, after.Offers.Select(o => o.LandedPrice));
        Assert.Equal(11, after.Offers.Single(o => o.OriginCityId == "ironvale").SupplierSnapshot!.Seq);
    }

    // ---- Conflicts ----

    [Fact]
    public void Same_seq_with_different_content_is_a_conflict()
    {
        var a = Scenarios.Ironvale();
        _f.Drop(a.Capture());
        _f.Store.IngestInbox();
        _f.ClearInbox();
        var before = _f.Snapshot();

        var other = Scenarios.Ironvale().Resource("Steel", 999, 1, 999).Capture(); // also seq 1
        _f.Drop(other);
        var o = IngestSingle();
        Assert.Equal((IngestStatus.Conflict, IngestReason.SameSeqDifferentContent), (o.Status, o.Reason));
        Assert.Null(o.RegionSeq);
        AssertNothingChanged(Without(_f.Snapshot(), "inbox"), Without(before, "inbox"));
    }

    [Fact]
    public void Older_seq_that_was_never_accepted_is_a_conflict()
    {
        var a = Scenarios.Ironvale();
        var s1 = a.Capture();
        var s2 = a.Capture();
        var s3 = a.Capture();
        s3.ParentSeq = 1; // an exporter that skipped publishing seq 2
        _f.Drop(s1); _f.Drop(s3);
        _f.Store.IngestInbox();
        _f.ClearInbox();

        _f.Drop(s2); // seq 2 arrives late: not newer than 3
        var o = IngestSingle();
        Assert.Equal((IngestStatus.Conflict, IngestReason.SeqNotNewer), (o.Status, o.Reason));
    }

    [Fact]
    public void Wrong_parent_is_a_fork_conflict()
    {
        var a = Scenarios.Ironvale();
        _f.Drop(a.Capture());
        _f.Store.IngestInbox();

        var fork = a.Capture();   // seq 2
        fork.ParentSeq = null;    // the exporter never saw seq 1
        _f.ClearInbox();
        _f.Drop(fork);
        var o = IngestSingle();
        Assert.Equal((IngestStatus.Conflict, IngestReason.ParentSeqMismatch), (o.Status, o.Reason));
    }

    [Fact]
    public void First_snapshot_claiming_a_parent_is_a_conflict()
    {
        var s = Scenarios.Ironvale().Capture();
        s.ParentSeq = 4;
        _f.Drop(s);
        Assert.Equal(IngestReason.ParentSeqMismatch, IngestSingle().Reason);
        Assert.Equal(0, _f.Store.ReadIndex().CurrentRegionSeq);
    }

    [Fact]
    public void Older_save_loaded_is_a_game_time_conflict()
    {
        var a = Scenarios.Ironvale();
        a.GameDays = 120;
        _f.Drop(a.Capture());
        _f.Store.IngestInbox();
        _f.ClearInbox();

        a.GameDays = 90; // the player reloaded an older save of the same city
        _f.Drop(a.Capture());
        var o = IngestSingle();
        Assert.Equal((IngestStatus.Conflict, IngestReason.GameTimeRollback), (o.Status, o.Reason));
        Assert.Contains("older save", o.Detail);
    }

    [Theory]
    [InlineData(null, 90.0)]
    [InlineData(120.0, null)]
    [InlineData(120.0, 120.0)]
    public void Unknown_or_equal_game_time_is_not_a_conflict(double? before, double? after)
    {
        var a = Scenarios.Ironvale();
        a.GameDays = before;
        _f.Drop(a.Capture());
        _f.Store.IngestInbox();
        _f.ClearInbox();
        a.GameDays = after;
        _f.Drop(a.Capture());
        Assert.Equal(IngestStatus.Accepted, IngestSingle().Status);
    }

    [Fact]
    public void A_conflict_does_not_block_other_cities()
    {
        var a = Scenarios.Ironvale();
        _f.Drop(a.Capture());
        _f.Store.IngestInbox();
        _f.ClearInbox();

        var fork = a.Capture();
        fork.ParentSeq = null;
        _f.Drop(fork);
        _f.Drop(Scenarios.Carford().Capture());
        var result = _f.Store.IngestInbox();
        Assert.Equal(new[] { IngestStatus.Accepted, IngestStatus.Conflict }, result.Outcomes.Select(o => o.Status)); // carford sorts first
        Assert.True(result.HasProblems);
        Assert.Equal(2, result.CurrentRegionSeq);
    }

    // ---- Rejected files ----

    [Fact]
    public void Truncated_file_is_rejected_as_corrupt()
    {
        var bytes = ContractJson.ToBytes(Scenarios.Ironvale().Capture());
        _f.DropBytes("ironvale.1.json", bytes[..(bytes.Length / 2)]);
        var before = _f.Snapshot();
        var o = IngestSingle();
        Assert.Equal((IngestStatus.Rejected, IngestReason.CorruptFile), (o.Status, o.Reason));
        AssertNothingChanged(before);
    }

    [Theory]
    [InlineData("\"seq\": 1,", "\"seq\": 1, \"bogus\": 1,")]                      // unknown property
    [InlineData("\"map\": \"Vanilla\"", "\"map\": 0")]                              // integer enum
    [InlineData("\"capacityEstimatePerGameDay\": 1200,", "")]                      // omitted field (null must be explicit)
    [InlineData("\"realizedOutputPerGameDay\": 1000", "\"realizedOutputPerGameDay\": -1")] // negative output
    public void Schema_invalid_file_is_rejected(string find, string replace)
    {
        var text = Encoding.UTF8.GetString(ContractJson.ToBytes(Scenarios.Ironvale().Capture()));
        Assert.Contains(find, text);
        _f.DropBytes("ironvale.1.json", Encoding.UTF8.GetBytes(text.Replace(find, replace)));
        var o = IngestSingle();
        Assert.Equal((IngestStatus.Rejected, IngestReason.SchemaInvalid), (o.Status, o.Reason));
        Assert.Equal(0, _f.Store.ReadIndex().CurrentRegionSeq);
    }

    [Theory]
    [InlineData("ironvale.json")]
    [InlineData("ironvale.0.json")]
    [InlineData("ironvale.one.json")]
    public void Bad_file_name_is_rejected(string name)
    {
        _f.DropBytes(name, ContractJson.ToBytes(Scenarios.Ironvale().Capture()));
        Assert.Equal(IngestReason.BadFileName, IngestSingle().Reason);
    }

    [Fact]
    public void File_name_must_match_content()
    {
        _f.DropBytes("ironvale.2.json", ContractJson.ToBytes(Scenarios.Ironvale().Capture()));
        Assert.Equal(IngestReason.FileNameMismatch, IngestSingle().Reason);
    }

    [Fact]
    public void City_not_in_manifest_is_rejected()
    {
        _f.Drop(new SyntheticCity("atlantis").Capture());
        Assert.Equal(IngestReason.UnknownCity, IngestSingle().Reason);
    }

    [Fact]
    public void Temp_and_non_json_files_are_ignored()
    {
        _f.DropBytes("ironvale.1.json.tmp", new byte[] { 1, 2, 3 });
        _f.DropBytes("notes.txt", new byte[] { 1 });
        Assert.Empty(_f.Store.IngestInbox().Outcomes);
    }

    private static Dictionary<string, string> Without(Dictionary<string, string> files, string folder) =>
        files.Where(kv => !kv.Key.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static void AssertNothingChanged(Dictionary<string, string> after, Dictionary<string, string> before) => Assert.Equal(before, after);
}
