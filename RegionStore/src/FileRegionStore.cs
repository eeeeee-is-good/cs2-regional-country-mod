using System.Globalization;
using System.Text.RegularExpressions;
using Regions.Contracts;
using Regions.Engine;

namespace Regions.Store;

/// <summary>
/// The region store in an external folder (Contracts/README.md "Region store layout"; ARCHITECTURE.md N3):
/// <code>
///   manifest.json                     RegionManifest; atomic replace; optimistic on revision
///   inbox/&lt;cityId&gt;.&lt;seq&gt;.json          written by exporters (create-only)
///   snapshots/index.json              StoreIndex; written only by this class
///   snapshots/&lt;cityId&gt;/&lt;seq&gt;.json      accepted snapshots; immutable
/// </code>
/// Rules: never touches save files; never overwrites or deletes a snapshot; never modifies inbox files
/// (ingestion is idempotent, so leaving them is safe); conflicts are reported, never resolved.
/// One writer per store at a time is assumed.
/// </summary>
public sealed class FileRegionStore
{
    private static readonly Regex InboxName = new(@"^(?<city>.+)\.(?<seq>[1-9][0-9]{0,8})\.json$", RegexOptions.CultureInvariant);

    private FileRegionStore(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string ManifestPath => Path.Combine(Root, "manifest.json");
    public string IndexPath => Path.Combine(Root, "snapshots", "index.json");
    public string InboxDirectory => Path.Combine(Root, "inbox");
    public string SnapshotPath(string cityId, int seq) => Path.Combine(Root, "snapshots", cityId, seq.ToString(CultureInfo.InvariantCulture) + ".json");
    public string InboxPath(string cityId, int seq) => Path.Combine(InboxDirectory, $"{cityId}.{seq.ToString(CultureInfo.InvariantCulture)}.json");

    /// <summary>Opens an existing store. Read-only until a write method is called.</summary>
    public static FileRegionStore Open(string root)
    {
        var store = new FileRegionStore(root);
        if (!Directory.Exists(store.Root)) throw new StoreException($"region folder not found: {store.Root}");
        if (!File.Exists(store.ManifestPath)) throw new StoreException($"not a region folder (no manifest.json): {store.Root}");
        if (!File.Exists(store.IndexPath)) throw new StoreException($"not a region folder (no snapshots/index.json): {store.Root}");
        return store;
    }

    /// <summary>Creates a new, empty store. Refuses an existing non-empty folder.</summary>
    public static FileRegionStore Create(string root, RegionManifest manifest)
    {
        var store = new FileRegionStore(root);
        if (Directory.Exists(store.Root) && Directory.EnumerateFileSystemEntries(store.Root).Any())
            throw new StoreException($"{store.Root} already exists and is not empty. Choose a new folder; nothing is overwritten.");

        var manifestBytes = ContractFiles.Serialize(manifest, ContractFiles.ManifestSchema, "manifest.json");
        var indexBytes = ContractFiles.Serialize(new StoreIndex { RegionId = manifest.RegionId }, ContractFiles.IndexSchema, "snapshots/index.json");
        Directory.CreateDirectory(store.InboxDirectory);
        ContractFiles.AtomicReplace(store.ManifestPath, manifestBytes);
        ContractFiles.AtomicReplace(store.IndexPath, indexBytes);
        return store;
    }

    // ---- Manifest ----

    public RegionManifest ReadManifest() => ContractFiles.ReadFile<RegionManifest>(ManifestPath, ContractFiles.ManifestSchema);

    /// <summary>
    /// Replaces the manifest if its current revision is <paramref name="expectedRevision"/>. The written manifest gets
    /// Revision = expectedRevision + 1 (the passed object is updated). The region id cannot change.
    /// </summary>
    public void WriteManifest(RegionManifest manifest, int expectedRevision)
    {
        var current = ReadManifest();
        if (current.Revision != expectedRevision)
            throw new ManifestConflictException($"manifest is at revision {current.Revision}, but the change was based on revision {expectedRevision}. Re-read and retry.");
        if (manifest.RegionId != current.RegionId)
            throw new StoreException($"cannot change regionId from '{current.RegionId}' to '{manifest.RegionId}'");

        manifest.Revision = expectedRevision + 1;
        ContractFiles.AtomicReplace(ManifestPath, ContractFiles.Serialize(manifest, ContractFiles.ManifestSchema, "manifest.json"));
    }

    // ---- Reading snapshots ----

    public StoreIndex ReadIndex()
    {
        var index = ContractFiles.ReadFile<StoreIndex>(IndexPath, ContractFiles.IndexSchema);
        var manifest = ReadManifest();
        if (index.RegionId != manifest.RegionId)
            throw new StoreCorruptException($"snapshots/index.json is for region '{index.RegionId}', manifest.json is '{manifest.RegionId}'");
        return index;
    }

    /// <summary>The latest accepted snapshot per city, each verified against its index hash. Sorted by city id.</summary>
    public IReadOnlyList<PublishedSnapshot> LatestSnapshots() => LatestSnapshots(ReadIndex());

    public RegionState LoadState(IEnumerable<ResourceDefinition> resources)
    {
        var index = ReadIndex();
        return new RegionState(ReadManifest(), LatestSnapshots(index), index.CurrentRegionSeq, resources);
    }

    private IReadOnlyList<PublishedSnapshot> LatestSnapshots(StoreIndex index) =>
        index.Snapshots.GroupBy(s => s.CityId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(s => s.Seq).First())
            .OrderBy(s => s.CityId, StringComparer.Ordinal)
            .Select(e => new PublishedSnapshot(ReadStored(e), e.RegionSeq))
            .ToList();

    private CitySnapshot ReadStored(StoredSnapshotInfo entry)
    {
        var path = SnapshotPath(entry.CityId, entry.Seq);
        if (!File.Exists(path)) throw new StoreCorruptException($"the index lists {entry.CityId} seq {entry.Seq}, but {path} is missing");
        var bytes = File.ReadAllBytes(path);
        var hash = ContractFiles.Sha256(bytes);
        if (hash != entry.FileSha256)
            throw new StoreCorruptException($"{path}: SHA-256 {hash} does not match the index ({entry.FileSha256}). The file was changed after it was accepted, or its line endings were converted.");
        CitySnapshot snapshot;
        try { snapshot = ContractFiles.Parse<CitySnapshot>(bytes, ContractFiles.CitySnapshotSchema, path); }
        catch (ContractFileException ex) { throw new StoreCorruptException(ex.Message); }
        if (snapshot.CityId != entry.CityId || snapshot.Seq != entry.Seq)
            throw new StoreCorruptException($"{path}: contains {snapshot.CityId} seq {snapshot.Seq}, the index says {entry.CityId} seq {entry.Seq}");
        return snapshot;
    }

    // ---- Ingestion ----

    /// <summary>
    /// Processes every inbox/*.json file (Contracts/README.md "Ingestion rules"). Deterministic order:
    /// valid names by (cityId ordinal, seq), then other names ordinally. Accepted files are copied byte-for-byte
    /// to snapshots/ first, then the index is replaced atomically once. A crash in between is recovered by the
    /// next run, because re-copying identical bytes is allowed. Inbox files are left as they are.
    /// </summary>
    public IngestResult IngestInbox()
    {
        var manifest = ReadManifest();
        var index = ReadIndex();
        var cities = new HashSet<string>(manifest.Cities.Select(c => c.CityId), StringComparer.Ordinal);
        var entries = index.Snapshots.ToList();
        var outcomes = new List<IngestOutcome>();
        var changed = false;

        var files = Directory.Exists(InboxDirectory)
            ? Directory.GetFiles(InboxDirectory, "*.json").Select(Path.GetFileName).Select(n => n!).ToList()
            : new List<string>();
        var parsed = files.Select(n => (Name: n, Match: InboxName.Match(n))).ToList();
        var ordered = parsed.Where(p => p.Match.Success)
            .OrderBy(p => p.Match.Groups["city"].Value, StringComparer.Ordinal)
            .ThenBy(p => int.Parse(p.Match.Groups["seq"].Value, CultureInfo.InvariantCulture))
            .Concat(parsed.Where(p => !p.Match.Success).OrderBy(p => p.Name, StringComparer.Ordinal));

        foreach (var (name, match) in ordered)
        {
            var outcome = IngestOne(name, match, cities, entries, index);
            outcomes.Add(outcome);
            changed |= outcome.Status == IngestStatus.Accepted;
        }

        if (changed)
        {
            index.Snapshots = entries.OrderBy(e => e.CityId, StringComparer.Ordinal).ThenBy(e => e.Seq).ToList();
            ContractFiles.AtomicReplace(IndexPath, ContractFiles.Serialize(index, ContractFiles.IndexSchema, "snapshots/index.json"));
        }
        return new IngestResult(outcomes, index.CurrentRegionSeq);
    }

    private IngestOutcome IngestOne(string name, Match match, HashSet<string> cities, List<StoredSnapshotInfo> entries, StoreIndex index)
    {
        if (!match.Success)
            return new IngestOutcome(name, null, null, IngestStatus.Rejected, IngestReason.BadFileName,
                null, "file name must be <cityId>.<seq>.json with seq >= 1");

        var nameCity = match.Groups["city"].Value;
        var nameSeq = int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture);
        var bytes = File.ReadAllBytes(Path.Combine(InboxDirectory, name));
        var hash = ContractFiles.Sha256(bytes);

        CitySnapshot s;
        try { s = ContractFiles.Parse<CitySnapshot>(bytes, ContractFiles.CitySnapshotSchema, name); }
        catch (ContractFileException ex)
        {
            return new IngestOutcome(name, nameCity, nameSeq, IngestStatus.Rejected,
                ex.IsCorrupt ? IngestReason.CorruptFile : IngestReason.SchemaInvalid, null, ex.Message);
        }

        IngestOutcome Out(IngestStatus st, IngestReason r, string detail, int? regionSeq = null) =>
            new(name, s.CityId, s.Seq, st, r, regionSeq, detail);

        if (s.CityId != nameCity || s.Seq != nameSeq)
            return Out(IngestStatus.Rejected, IngestReason.FileNameMismatch, $"file name says {nameCity} seq {nameSeq}, content says {s.CityId} seq {s.Seq}");
        if (!cities.Contains(s.CityId))
            return Out(IngestStatus.Rejected, IngestReason.UnknownCity, $"city '{s.CityId}' is not in manifest.json");

        var same = entries.FirstOrDefault(e => e.CityId == s.CityId && e.Seq == s.Seq);
        if (same != null)
            return same.FileSha256 == hash
                ? Out(IngestStatus.Duplicate, IngestReason.AlreadyIngested, $"already accepted as regionSeq {same.RegionSeq}; nothing to do", same.RegionSeq)
                : Out(IngestStatus.Conflict, IngestReason.SameSeqDifferentContent, $"seq {s.Seq} was already accepted with different content (hash {same.FileSha256[..12]}…, this file {hash[..12]}…)");

        var latest = entries.Where(e => e.CityId == s.CityId).OrderByDescending(e => e.Seq).FirstOrDefault();
        if (latest != null && s.Seq <= latest.Seq)
            return Out(IngestStatus.Conflict, IngestReason.SeqNotNewer, $"seq {s.Seq} is not newer than the latest accepted seq {latest.Seq} (rollback or fork, log §22.3)");
        if (s.ParentSeq != latest?.Seq)
            return Out(IngestStatus.Conflict, IngestReason.ParentSeqMismatch,
                $"parentSeq is {S(s.ParentSeq)}, but the latest accepted seq is {S(latest?.Seq)}. The exporter did not see the latest publication (fork, log §22.6)");

        if (latest != null)
        {
            var previous = ReadStored(latest);
            if (previous.GameTime.GameDays is double before && s.GameTime.GameDays is double now && now < before)
                return Out(IngestStatus.Conflict, IngestReason.GameTimeRollback,
                    $"game time went back from day {before.ToString(CultureInfo.InvariantCulture)} to day {now.ToString(CultureInfo.InvariantCulture)}: an older save was probably loaded");
        }

        ContractFiles.CreateImmutable(SnapshotPath(s.CityId, s.Seq), bytes);
        index.CurrentRegionSeq += 1;
        entries.Add(new StoredSnapshotInfo { CityId = s.CityId, Seq = s.Seq, RegionSeq = index.CurrentRegionSeq, FileSha256 = hash });
        return Out(IngestStatus.Accepted, IngestReason.Accepted, $"accepted as regionSeq {index.CurrentRegionSeq}", index.CurrentRegionSeq);
    }

    private static string S(int? i) => i?.ToString(CultureInfo.InvariantCulture) ?? "none";
}
