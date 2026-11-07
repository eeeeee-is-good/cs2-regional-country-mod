using System.Text;
using System.Text.Json.Nodes;
using Regions.Contracts;
using Regions.Contracts.Tests;
using Regions.Engine;
using Xunit;
using Cli = Regions.ScenarioRunner.Cli;
using CliJson = Regions.ScenarioRunner.ContractJson;
using RegionFolder = Regions.ScenarioRunner.RegionFolder;

namespace Regions.ScenarioTests;

public sealed class CliTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "regions-cli-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Golden = Repo.Golden("v1-basic");

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Help_and_scenario_list()
    {
        var help = Run("help");
        Assert.Equal(Cli.Ok, help.Code);
        Assert.Contains("regions quote <folder>", help.Out);

        var list = Run("scenarios");
        Assert.Equal(Cli.Ok, list.Code);
        foreach (var e in Simulator.Scenarios.All) Assert.Contains(e.Name, list.Out);
    }

    [Fact]
    public void Quote_on_golden_fixture_explains_the_price()
    {
        var r = Run("quote", Golden, "--dest", "city-b", "--resource", "Steel");
        Assert.Equal(Cli.Ok, r.Code);
        Assert.Contains("Steel delivered to Carford (consumer) [city-b]", r.Out);
        Assert.Contains("Applied to game: no.", r.Out);
        Assert.Contains("excluded: no usable route", r.Out);
        Assert.Contains("Cheapest: #0 Ironvale (producer) at 134.00", r.Out);
        Assert.Empty(r.Err);
    }

    [Fact]
    public void Quote_output_is_identical_on_every_run()
    {
        Assert.Equal(Run("quote", Golden).Out, Run("quote", Golden).Out);
    }

    [Fact]
    public void Json_output_is_the_engine_quote_and_schema_valid()
    {
        var outDir = Path.Combine(_temp, "out");
        Assert.Equal(Cli.Ok, Run("quote", Golden, "--dest", "city-b", "--json", outDir).Code);
        var file = Path.Combine(outDir, "quote.city-b.Steel.json");
        var bytes = File.ReadAllBytes(file);

        var expected = new LandedCostEngine().Quote(RegionFolder.Load(Golden), new QuoteRequest { DestCityId = "city-b", ResourceId = "Steel" });
        Assert.Equal(CliJson.ToBytes(expected), bytes);
        Schemas.AssertValid("quote-result.v1.schema.json", JsonNode.Parse(bytes), file);
        AssertContractBytes(bytes, file);
    }

    [Fact]
    public void Check_on_golden_fixture_passes()
    {
        var r = Run("check", Golden);
        Assert.Equal(Cli.Ok, r.Code);
        Assert.Contains("All invariants hold.", r.Out);
    }

    public static IEnumerable<object[]> Names() => Simulator.Scenarios.All.Select(e => new object[] { e.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Written_scenarios_load_quote_and_check(string name)
    {
        var dir = Path.Combine(_temp, name);
        var write = Run("scenario", name, dir);
        Assert.True(write.Code == Cli.Ok, write.Err);

        foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)) AssertContractBytes(File.ReadAllBytes(f), f);

        var quote = Run("quote", dir, "--dest", Simulator.Scenarios.Destination, "--resource", Simulator.Scenarios.Resource);
        Assert.True(quote.Code == Cli.Ok, quote.Err);
        Assert.Contains("Cheapest:", quote.Out);

        var check = Run("check", dir);
        Assert.True(check.Code == Cli.Ok, check.Out + check.Err);

        // The folder round-trips to the same quote as the in-memory scenario.
        var fromDisk = new LandedCostEngine().Quote(RegionFolder.Load(dir), new QuoteRequest { DestCityId = "carford", ResourceId = "Steel" });
        var inMemory = new LandedCostEngine().Quote(Simulator.Scenarios.Build(name).ToState(), new QuoteRequest { DestCityId = "carford", ResourceId = "Steel" });
        Assert.Equal(CliJson.ToBytes(inMemory), CliJson.ToBytes(fromDisk));
    }

    [Fact]
    public void Writing_never_overwrites_an_existing_folder()
    {
        var dir = Path.Combine(_temp, "G01");
        Assert.Equal(Cli.Ok, Run("scenario", "G01", dir).Code);
        var manifest = Path.Combine(dir, "manifest.json");
        File.AppendAllText(manifest, " ");
        var before = File.ReadAllBytes(manifest);

        var again = Run("scenario", "G03", dir);
        Assert.Equal(Cli.UsageOrInputError, again.Code);
        Assert.Contains("not empty", again.Err);
        Assert.Equal(before, File.ReadAllBytes(manifest));
    }

    [Fact]
    public void Tampered_snapshot_is_rejected_by_hash()
    {
        var dir = CopyGolden();
        var snap = Path.Combine(dir, "snapshots", "city-a", "1.json");
        File.WriteAllText(snap, File.ReadAllText(snap).Replace("\"realizedOutputPerGameDay\": 1000", "\"realizedOutputPerGameDay\": 9000"));
        var r = Run("quote", dir);
        Assert.Equal(Cli.UsageOrInputError, r.Code);
        Assert.Contains("SHA-256", r.Err);
    }

    [Fact]
    public void Schema_invalid_manifest_is_rejected()
    {
        var dir = CopyGolden();
        var manifest = Path.Combine(dir, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"revision\": 1,", "\"revision\": 1, \"bogus\": true,"));
        var r = Run("check", dir);
        Assert.Equal(Cli.UsageOrInputError, r.Code);
        Assert.Contains("does not match region-manifest.v1.schema.json", r.Err);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("quote")]
    [InlineData("scenario", "G01")]
    [InlineData("scenario", "nope", "x")]
    [InlineData("quote", "does-not-exist")]
    [InlineData("quote", "GOLDEN", "--dest", "city-z")]
    [InlineData("quote", "GOLDEN", "--wat", "1")]
    [InlineData("quote", "GOLDEN", "--dest")]
    public void Bad_usage_gives_exit_code_2_and_a_message(params string[] args)
    {
        var r = Run(args.Select(a => a == "GOLDEN" ? Golden : a).ToArray());
        Assert.Equal(Cli.UsageOrInputError, r.Code);
        Assert.NotEmpty(r.Err);
    }

    private string CopyGolden()
    {
        var dir = Path.Combine(_temp, "golden-copy");
        foreach (var src in Directory.GetFiles(Golden, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dir, Path.GetRelativePath(Golden, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest);
        }
        return dir;
    }

    /// <summary>Contracts/README.md rule 1: UTF-8 without BOM, LF line endings.</summary>
    private static void AssertContractBytes(byte[] bytes, string label)
    {
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{label}: has a BOM");
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(bytes));
    }
}
