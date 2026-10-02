using Raffaello.Core.Integrations.CadExchange;

namespace Raffaello.Core.Tests;

/// <summary>[trust] The block map shipped with the add-ins parses with the shared model.</summary>
public sealed class TrustCadBlockMapTests
{
    [Fact]
    public void Shipped_blockmap_is_valid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Raffaello.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var map = CadExchangeJson.ReadBlockMap(Path.Combine(dir!.FullName, "tools", "cad", "blockmap.json"));
        Assert.True(map.Rules.Count >= 10);
        Assert.Equal(2, map.Match("TWIN-DATA-OUTLET", "E-DATA")!.Qty);
        Assert.True(map.Match("A1-TITLE-BLOCK", "0")!.Ignore);
        Assert.Equal("2ND FIX", map.Match("THERMOSTAT-CP4", "E-GRMS")!.Stage);
    }
}
