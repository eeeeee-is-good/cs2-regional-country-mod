using Regions.Engine;
using Regions.Simulator;
using Regions.Store;
using Xunit;

namespace Regions.StoreTests;

public sealed class SignalStoreTests : IDisposable
{
    private readonly StoreFixture _f = new();
    public void Dispose() => _f.Dispose();

    private void PublishBoth()
    {
        _f.Drop(Scenarios.Ironvale().Capture());
        _f.Drop(Scenarios.Carford().Capture());
        _f.Store.IngestInbox();
    }

    [Fact]
    public void No_state_before_the_first_period()
    {
        PublishBoth();
        Assert.Null(_f.Store.ReadSignals());
    }

    [Fact]
    public void Periods_persist_and_continue()
    {
        PublishBoth();
        var first = _f.Store.AdvancePeriod(ResourceTable.Load(), new SignalRule());
        Assert.Equal(1, first.Period);
        Assert.Equal(first.Period, _f.Store.ReadSignals()!.Period);
        var third = _f.Store.AdvancePeriod(ResourceTable.Load(), new SignalRule());
        third = _f.Store.AdvancePeriod(ResourceTable.Load(), new SignalRule());
        Assert.Equal(3, third.Period);
        Assert.Equal(134, third.Signals.Single(s => s.CityId == "carford").Buy.Applied);
        Assert.Empty(Directory.GetFiles(_f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corrupt_state_is_reported()
    {
        PublishBoth();
        _f.Store.AdvancePeriod(ResourceTable.Load(), new SignalRule());
        File.WriteAllText(_f.Store.SignalsPath, "{");
        Assert.Throws<StoreCorruptException>(() => _f.Store.ReadSignals());
    }
}
