using Regions.Contracts;
using Regions.Engine;
using Regions.Simulator;
using Regions.Store;

namespace Regions.ScenarioRunner;

/// <summary>Entry point logic, separate from Main so tests can run it in-process.</summary>
public static class Cli
{
    public const int Ok = 0;
    public const int CheckFailed = 1;
    public const int UsageOrInputError = 2;

    public const string Usage = """
        regions - test the regional engine without Cities: Skylines II

        Usage:
          regions scenarios                     List the built-in synthetic regions.
          regions scenario <name> <folder>      Write a built-in region to a new folder.
          regions quote <folder> [options]      Show landed-cost quotes and why each offer won or lost.
            --dest <cityId>                       Only this destination city (default: every city).
            --resource <id>                       Only this resource (default: every resource seen).
            --json <outFolder>                    Also write quote.<dest>.<resource>.json files.
          regions check <folder>                Quote everything and check the log §18.4 invariants.
          regions ingest <folder>               Accept new snapshot files from <folder>/inbox and report conflicts.

          --resources <file>                    Resource table to use (default: the bundled resource-ids.v1.json).

        A region folder holds manifest.json, inbox/, snapshots/index.json and snapshots/<cityId>/<seq>.json
        (Contracts/README.md "Region store layout"). Contracts/golden/v1-basic is one.
        Quotes are modeled price signals. Nothing here reads or writes a game save.
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (positional, options) = Parse(args);
            var command = positional.Count == 0 ? "help" : positional[0].ToLowerInvariant();
            switch (command)
            {
                case "help": case "--help": case "-h":
                    stdout.Write(Usage.Replace("\r\n", "\n") + "\n");
                    return Ok;

                case "scenarios":
                    foreach (var e in Scenarios.All) stdout.Write($"  {e.Name,-9} {e.Description}\n");
                    return Ok;

                case "scenario":
                    Require(positional, 3, "regions scenario <name> <folder>");
                    var scenario = Scenarios.Build(positional[1]);
                    RegionFolder.Write(scenario, positional[2]);
                    stdout.Write($"Wrote scenario {scenario.Name} to {positional[2]}\n  {scenario.Description}\n" +
                                 $"Try: regions quote \"{positional[2]}\" --dest {Scenarios.Destination} --resource {Scenarios.Resource}\n");
                    return Ok;

                case "quote":
                {
                    Require(positional, 2, "regions quote <folder>");
                    var state = RegionFolder.Load(positional[1], options.GetValueOrDefault("resources"));
                    var engine = new LandedCostEngine();
                    var quotes = Requests(state, options).Select(r => engine.Quote(state, r)).Where(q => q.Offers.Count > 0).ToList();
                    if (quotes.Count == 0) { stdout.Write("No quotes: no city has any offer for the requested resource(s).\n"); return Ok; }
                    for (var i = 0; i < quotes.Count; i++)
                    {
                        if (i > 0) stdout.Write("\n");
                        stdout.Write(TextReport.Quote(quotes[i], state));
                        if (options.TryGetValue("json", out var outDir)) RegionFolder.WriteQuote(quotes[i], outDir);
                    }
                    return Ok;
                }

                case "check":
                {
                    Require(positional, 2, "regions check <folder>");
                    var state = RegionFolder.Load(positional[1], options.GetValueOrDefault("resources"));
                    var report = new LandedCostEngine().Check(state);
                    stdout.Write(TextReport.Check(report, state.Manifest));
                    return report.Passed ? Ok : CheckFailed;
                }

                case "ingest":
                {
                    Require(positional, 2, "regions ingest <folder>");
                    var result = FileRegionStore.Open(positional[1]).IngestInbox();
                    if (result.Outcomes.Count == 0) stdout.Write("Inbox is empty.\n");
                    foreach (var o in result.Outcomes) stdout.Write("  " + o + "\n");
                    stdout.Write($"{result.Accepted} accepted. Region is at regionSeq {result.CurrentRegionSeq}.\n");
                    if (result.HasProblems)
                        stdout.Write("Conflicts and rejected files are never resolved automatically. Inbox files are left in place; remove or replace them yourself.\n");
                    return result.HasProblems ? CheckFailed : Ok;
                }

                default:
                    stderr.Write($"Unknown command '{positional[0]}'.\n\n{Usage.Replace("\r\n", "\n")}\n");
                    return UsageOrInputError;
            }
        }
        catch (Exception ex) when (ex is StoreException or RegionStateException or UsageException or ArgumentException or IOException)
        {
            stderr.Write("error: " + ex.Message + "\n");
            return UsageOrInputError;
        }
    }

    private static IEnumerable<QuoteRequest> Requests(RegionState state, Dictionary<string, string> options)
    {
        var dests = options.TryGetValue("dest", out var d)
            ? new List<string> { d }
            : state.Manifest.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var resources = options.TryGetValue("resource", out var r)
            ? new List<string> { r }
            : state.Manifest.Parameters.ResourcePrices.Select(p => p.ResourceId)
                .Concat(state.LatestSnapshots.SelectMany(s => s.Snapshot.Resources.Select(x => x.ResourceId)))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var dest in dests)
            foreach (var res in resources)
                yield return new QuoteRequest { DestCityId = dest, ResourceId = res };
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    private static void Require(List<string> positional, int count, string usage)
    {
        if (positional.Count < count) throw new UsageException($"usage: {usage}");
    }

    private static readonly HashSet<string> KnownOptions = new() { "dest", "resource", "json", "resources" };

    private static (List<string>, Dictionary<string, string>) Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "--help") { positional.Add(args[i]); continue; }
            var name = args[i][2..];
            if (!KnownOptions.Contains(name)) throw new UsageException($"unknown option --{name}");
            if (i + 1 >= args.Length) throw new UsageException($"--{name} needs a value");
            options[name] = args[++i];
        }
        return (positional, options);
    }
}
