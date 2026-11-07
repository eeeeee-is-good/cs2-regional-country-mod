using Regions.Contracts;
using Regions.Simulator;
using Regions.Store;

namespace Regions.StoreTests;

/// <summary>A fresh store in a temp folder with two cities (ironvale, carford). Deleted on dispose.</summary>
public sealed class StoreFixture : IDisposable
{
    public StoreFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "regions-store-tests-" + Guid.NewGuid().ToString("N"));
        var manifest = Scenarios.Build("G03").SortManifest().Manifest;
        Store = FileRegionStore.Create(Root, manifest);
    }

    public string Root { get; }
    public FileRegionStore Store { get; }

    /// <summary>Exporter side: creates inbox/&lt;cityId&gt;.&lt;seq&gt;.json with exactly these bytes.</summary>
    public string Drop(CitySnapshot snapshot) => DropBytes($"{snapshot.CityId}.{snapshot.Seq}.json", ContractJson.ToBytes(snapshot));

    public string DropBytes(string fileName, byte[] bytes)
    {
        var path = Path.Combine(Store.InboxDirectory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void ClearInbox()
    {
        foreach (var f in Directory.GetFiles(Store.InboxDirectory)) File.Delete(f);
    }

    public byte[] IndexBytes() => File.ReadAllBytes(Store.IndexPath);

    /// <summary>Every file under the store, with its bytes, for "nothing changed" assertions.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Root, f), f => ContractFiles.Sha256(File.ReadAllBytes(f)));

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
