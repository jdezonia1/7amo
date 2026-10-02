using Raffaello.Core.Cables;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

/// <summary>[cables] Panel-name normaliser, fuzzy matcher and cable-size parser.</summary>
public class CablesNamingTests
{
    [Theory]
    [InlineData("SMDB HT-Z1-LB2- CM 01")]
    [InlineData("SMDB-HT-Z1-LB2-CM-01")]
    [InlineData("smdb-ht-z1-lb2-cm-1")]
    [InlineData("SMDB-HT-Z1-LBO2-CM-O1")]
    [InlineData("SMDB - HT - Z01 - LB2 - CM01")]
    [InlineData("SMDB-LB2-HT-Z1-CM-01")]
    [InlineData("SMDB_HT.Z1/LB2  CM 01")]
    public void Spellings_of_one_panel_get_the_same_key(string raw) =>
        Assert.Equal("SMDB-HT-Z1-LB2-CM-1", PanelNames.KeyOf(raw));

    [Fact]
    public void Parse_splits_type_building_zone_level_suffix()
    {
        var p = PanelNames.Parse("EMCC-BR-Z2-LB1-FANS");
        Assert.Equal(("EMCC", "BR", "Z2", "LB1"), (p.Type, p.Building, p.Zone, p.Level));
        Assert.Equal(new[] { "FANS" }, p.Suffix);
        Assert.True(p.IsStructured);
        Assert.True(p.IsPanelType);
        Assert.Equal("L0", PanelNames.Parse("SMDB-HT-Z4-L00-GR 01").Level);
        Assert.True(PanelNames.Parse("SMDB").IsIncomplete);
        Assert.False(PanelNames.Parse("JF-LB1-04").IsPanelType);   // jet fan = equipment
    }

    [Fact]
    public void Building_scopes_names_without_a_building_token()
    {
        Assert.NotEqual(PanelNames.KeyOf("EV CHARGER-04", Buildings.Hotel), PanelNames.KeyOf("EV CHARGER-04", Buildings.Branded));
        Assert.Equal(PanelNames.KeyOf("SMDB-BR-Z3-LB1-01"), PanelNames.KeyOf("SMDB-Z3-LB1-01", Buildings.Branded));
        Assert.Equal(PanelNames.KeyOf("SMDB-BR-Z3-LB1-01"), PanelNames.KeyOf("SMDB-BR-Z3-LB1-01", Buildings.Hotel));   // the name's own token wins
    }

    [Fact]
    public void Similar_names_are_suggested_but_numbered_or_different_panels_are_not()
    {
        Assert.True(PanelNames.Similarity("EMCC HT-Z1-LB2- FAN", "EMCC HT-Z1-LB2- FANS") >= 0.8);
        Assert.Equal(1, PanelNames.Similarity("SMDB HT-Z1-LB2- CM 01", "SMDB-HT-Z1-LB2-CM-01"));
        Assert.Equal(0, PanelNames.Similarity("SMDB-HT-Z1-LB2-KT-01", "SMDB-HT-Z1-LB2-KT-02"));
        Assert.Equal(0, PanelNames.Similarity("FFP-LL2-J-01", "FFP-LL2-D-01"));
        Assert.Equal(0, PanelNames.Similarity("SMDB-HT-Z1-LB2-CM-01", "SMDB-HT-Z2-LB2-CM-01"));   // zone differs
        Assert.Equal(0, PanelNames.Similarity("SMDB-HT-Z1-LB2-CM-01", "ESMDB-HT-Z1-LB2-CM-01"));  // type differs
        Assert.Equal(0, PanelNames.Similarity("SMDB-HT-Z1-LB2-CM-01", "SMDB-BR-Z1-LB2-CM-01"));   // building differs
    }

    [Fact]
    public void Looks_like_panel()
    {
        Assert.True(PanelNames.LooksLikePanel("LDB-HT-Z1-LB2-03"));
        Assert.True(PanelNames.LooksLikePanel("JF-LB1-04"));
        Assert.False(PanelNames.LooksLikePanel("4X16"));
        Assert.False(PanelNames.LooksLikePanel("ROOM NO 27"));
        Assert.False(PanelNames.LooksLikePanel(""));
    }

    [Fact]
    public void Resolver_uses_exact_key_then_learned_alias_and_only_suggests_fuzzy()
    {
        var panels = new[]
        {
            new CablePanel { Name = "SMDB-HT-Z1-LB2-CM-01", Key = PanelNames.KeyOf("SMDB-HT-Z1-LB2-CM-01") },
            new CablePanel { Name = "EMCC-HT-Z1-LB2-FANS", Key = PanelNames.KeyOf("EMCC-HT-Z1-LB2-FANS") },
        };
        var aliases = new[] { new CablePanelAlias { AliasKey = PanelNames.KeyOf("MAIN KITCHEN SMDB HT"), PanelKey = panels[0].Key, Kind = "USER" } };
        var r = new PanelResolver(panels, aliases);
        Assert.Equal("EXACT", r.Resolve("SMDB HT-Z1-LB2- CM 01").How);
        var a = r.Resolve("MAIN KITCHEN SMDB HT");
        Assert.Equal("ALIAS", a.How);
        Assert.Same(panels[0], a.Panel);
        var n = r.Resolve("EMCC HT-Z1-LB2- FAN");
        Assert.Equal("NEW", n.How);
        Assert.Null(n.Panel);
        Assert.Contains(n.Suggestions, s => s.Panel == panels[1]);
    }

    [Theory]
    [InlineData("4x16", "4X16", 4, 16.0, "")]
    [InlineData("4X16", "4X16", 4, 16.0, "")]
    [InlineData("4C x 16mm²", "4X16", 4, 16.0, "")]
    [InlineData("4Cx16mm2", "4X16", 4, 16.0, "")]
    [InlineData("4 core 16 sq.mm", "4X16", 4, 16.0, "")]
    [InlineData("4x240+1x120 E", "4X240", 4, 240.0, "1X120")]
    [InlineData("4C X 240 + E120", "4X240", 4, 240.0, "1X120")]
    [InlineData("(4x1C)x240mm²", "4X1CX240", 4, 240.0, "")]
    [InlineData("3.5Cx185", "3.5X185", 4, 185.0, "")]
    [InlineData("1x16", "1X16", 1, 16.0, "")]
    public void Cable_sizes_are_normalised(string raw, string key, int cores, double mm2, string earth)
    {
        var s = CableSize.Parse(raw);
        Assert.True(s.IsValid, raw);
        Assert.Equal(key, s.Key);
        Assert.Equal(cores, s.Cores);
        Assert.Equal(mm2, s.Mm2);
        Assert.Equal(earth, s.EarthKey);
    }

    [Fact]
    public void Cable_size_attributes_earth_parallel_conductor_insulation()
    {
        Assert.True(CableSize.Parse("1x16 E").IsEarthOnly);
        Assert.True(CableSize.Parse("1C x 16mm² ECC").IsEarthOnly);
        Assert.False(CableSize.Parse("1x16").IsEarthOnly);
        Assert.Equal("2X(4X240)", CableSize.Parse("2x(4x240)").Key);
        Assert.Equal("AL", CableSize.Parse("4X25 AL XLPE").Conductor);
        Assert.Equal("MICA", CableSize.Parse("4C x 16mm² MICA FR").Insulation);
        Assert.Equal("LSOH", CableSize.Parse("4C x 16mm² CU/XLPE/LSOH").Insulation);
        Assert.False(CableSize.Parse("SMDB-HT-Z1-LB2-CM-01").IsValid);
        Assert.False(CableSize.Parse("").IsValid);
    }

    [Fact]
    public void Sizes_are_found_in_and_stripped_from_free_text()
    {
        var found = CableSize.FindAll("FEEDER 4C x 16mm² CU/XLPE + 1x16 E 63A TP MCCB").ToList();
        Assert.Single(found);
        Assert.Equal("4X16", found[0].Key);
        Assert.Equal("1X16", found[0].EarthKey);
        Assert.Equal("SMDB-HT-Z1-LB2-CM-01", CableSize.Strip("SMDB-HT-Z1-LB2-CM-01 4x16"));
        Assert.False(CableSize.Contains("SMDB-HT-Z1-LB2-CM-01"));
    }

    [Fact]
    public void Stages_normalise_and_carry_the_70_20_10_split()
    {
        Assert.Equal(CableStages.Pulling, CableStages.Normalize("CABLE PULLING"));
        Assert.Equal(CableStages.Termination, CableStages.Normalize("termination & test"));
        Assert.Equal(CableStages.Termination, CableStages.Normalize("TESTING"));
        Assert.Equal(CableStages.Handover, CableStages.Normalize("Initial handover"));
        Assert.Equal(1.0, CableStages.All.Sum(CableStages.Pct), 6);
        Assert.Equal(0.7, CableStages.Pct("CABLE PULLING"));
    }
}
