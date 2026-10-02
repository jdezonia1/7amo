using System.Net.Http.Json;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Remote;

namespace Raffaello.Server.Tests;

/// <summary>[assemblies] The assembly store against the Raffaello server (tables, unique template code guard, parse endpoint).</summary>
public sealed class AssembliesServerTests
{
    [PgFact]
    public async Task Assembly_store_seeds_saves_and_guards_template_codes_on_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("qs-a");
        using var b = srv.Client("qs-b");
        var storeA = new RemoteAssemblyStore(a);
        var storeB = new RemoteAssemblyStore(b);

        var lib = storeA.LoadLibrary();                       // seeds the defaults
        Assert.Equal(DefaultLibrary.Build().Templates.Count, lib.Templates.Count);
        var light = storeB.LoadLibrary().ByCode("LIGHT-PT")!;
        Assert.NotEmpty(light.Components);
        Assert.All(light.Components, c => Assert.Equal(light.Header.Id, c.TemplateId));   // temp ids remapped

        light.Params.Single(p => p.Name == "route_len").Value = 8;
        storeB.SaveTemplate(light);
        Assert.Equal(8, storeA.LoadLibrary().ByCode("LIGHT-PT")!.Params.Single(p => p.Name == "route_len").Value);

        // a raw insert with a duplicate code is refused by the server guard
        var ex = Assert.Throws<RemoteRejectedException>(() => a.Insert(new AsmTemplate { Code = "light-pt", Name = "dup", ItemType = ItemTypes.LightingPoint }));
        Assert.Equal("duplicate", ex.Error.Code);

        storeA.SavePrice(new AsmPrice { Description = "PVC conduit 20 mm", Unit = "M", Price = 2.1, Source = PriceSources.Manual });
        Assert.Single(storeB.Prices());
        var s = storeA.LoadSettings(); s.OverheadPct = 0.15; storeA.SaveSettings(s);
        Assert.Equal(0.15, storeB.LoadSettings().OverheadPct);

        using var http = srv.Http("qs-a");
        var resp = await http.PostAsJsonAsync("/api/v1/assemblies/parse", new { Description = "Pull and hand over cable 16 mm² (4C or 3×16)", Unit = "mt", Source = "CONTRACT" });
        resp.EnsureSuccessStatusCode();
        var spec = await resp.Content.ReadFromJsonAsync<ItemSpec>();
        Assert.Equal(ItemTypes.CableRun, spec!.ItemType);
        Assert.Equal(16, spec.CableSizeMm2);
    }
}
