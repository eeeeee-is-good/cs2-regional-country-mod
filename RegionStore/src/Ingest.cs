namespace Regions.Store;

public enum IngestStatus
{
    /// <summary>Copied into snapshots/ and given a RegionSeq.</summary>
    Accepted,
    /// <summary>Same (cityId, seq) and same bytes as an accepted snapshot: a no-op (G06).</summary>
    Duplicate,
    /// <summary>Well-formed but inconsistent with the city's history. Reported, never resolved silently.</summary>
    Conflict,
    /// <summary>The file itself is unusable.</summary>
    Rejected,
}

public enum IngestReason
{
    Accepted,
    AlreadyIngested,
    // Conflicts (Contracts/README.md ingestion table, log §22.3)
    SameSeqDifferentContent,
    SeqNotNewer,
    ParentSeqMismatch,
    GameTimeRollback,
    // Rejections
    CorruptFile,
    SchemaInvalid,
    BadFileName,
    FileNameMismatch,
    UnknownCity,
}

public sealed class IngestOutcome
{
    public IngestOutcome(string fileName, string? cityId, int? seq, IngestStatus status, IngestReason reason, int? regionSeq, string detail)
    {
        FileName = fileName; CityId = cityId; Seq = seq; Status = status; Reason = reason; RegionSeq = regionSeq; Detail = detail;
    }

    /// <summary>File name inside inbox/.</summary>
    public string FileName { get; }
    public string? CityId { get; }
    public int? Seq { get; }
    public IngestStatus Status { get; }
    public IngestReason Reason { get; }
    /// <summary>Assigned RegionSeq for Accepted; the existing one for Duplicate; otherwise null.</summary>
    public int? RegionSeq { get; }
    public string Detail { get; }

    public override string ToString() => $"{Status,-9} {FileName}: {Detail}";
}

public sealed class IngestResult
{
    public IngestResult(IReadOnlyList<IngestOutcome> outcomes, int currentRegionSeq)
    {
        Outcomes = outcomes;
        CurrentRegionSeq = currentRegionSeq;
    }

    /// <summary>In processing order: valid file names by (cityId ordinal, seq), then unparsable names ordinally.</summary>
    public IReadOnlyList<IngestOutcome> Outcomes { get; }
    public int CurrentRegionSeq { get; }
    public int Accepted => Outcomes.Count(o => o.Status == IngestStatus.Accepted);
    public bool HasProblems => Outcomes.Any(o => o.Status is IngestStatus.Conflict or IngestStatus.Rejected);
}
