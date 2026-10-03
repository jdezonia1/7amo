using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

/// <summary>Gas meters are done by the electrical subcontractor (03-Oct); link-table check across systems.</summary>
public class LinkTableCheckTests
{
    private static ContractItem Item(string no, string section, string desc) => new() { ContractNo = "C1", ItemNo = no, Section = section, Description = desc };
    private static ContractItemBoq Link(string no, string code, string desc = "") => new() { ContractNo = "C1", ItemNo = no, BoqCode = code, BoqDescription = desc };

    [Fact]
    public void Metering_items_move_from_the_bms_lump_sum_to_the_gas_meter_code_of_the_bill()
    {
        var items = new[] { Item("267", "Metering System", "PVC 1st Fix outlet for Metering"), Item("10", "Lighting & Power", "lighting point") };
        var links = new List<ContractItemBoq>
        {
            Link("267", "B3-01-01-00-3-22-L-12", "BMS system"), Link("267", "B6-01-01-00-6-22-X-18", "BMS system"),
            Link("10", "B3-01-01-00-3-22-L-12", "BMS system"),                              // not a metering item: left alone
        };
        var log = LinkTableCheck.FixGasMeterLinks(items, links);
        Assert.Equal(2, log.Count);
        Assert.Equal("B3-01-01-00-3-22-E-12", links[0].BoqCode);
        Assert.Equal("B6-01-01-00-6-22-R-18", links[1].BoqCode);
        Assert.Equal("B3-01-01-00-3-22-L-12", links[2].BoqCode);
        Assert.Contains(LinkTableCheck.Check(items, links), w => w.ItemNo == "10" && w.Message.Contains("BMS"));
        Assert.DoesNotContain(LinkTableCheck.Check(items, links), w => w.ItemNo == "267");
    }

    [Fact]
    public void Warns_only_across_system_families()
    {
        var items = new[]
        {
            Item("1", "Lighting & Power", "1st fix lighting and power point"),
            Item("2", "Fire Alarm", "fire alarm devices"),
            Item("3", "Data / CCTV / TV", "floor box data outlet"),
            Item("4", "Lighting & Power", "lighting point"),
        };
        var links = new[]
        {
            Link("1", "B3-01-01-00-3-26-P-5", "small power points"),                      // power & lighting family: fine
            Link("2", "B3-01-01-00-3-26-A-11", "fire telephone input jack"),              // fire telephone is fire: fine
            Link("2", "B2-01-01-00-2-27-T-8", "to CCTV camera points"),                   // fire item -> CCTV: warning
            Link("3", "B3-01-01-00-3-26-X-15", "FB, floor box"),                          // floor box carries data: fine
            Link("4", "B2-01-01-00-2-22-A-3", "160 mm diameter"),                         // mechanical division: warning
        };
        var w = LinkTableCheck.Check(items, links);
        Assert.Equal(2, w.Count);
        Assert.Contains(w, x => x.ItemNo == "2" && x.BoqCode.EndsWith("T-8"));
        Assert.Contains(w, x => x.ItemNo == "4" && x.Message.Contains("mechanical"));
    }
}
