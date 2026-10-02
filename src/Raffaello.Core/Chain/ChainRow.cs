using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Chain;

/// <summary>
/// The chain for one line: QS / PROJECT QTY -> GIVEN -> DONE (WIR) -> CLAIMED (invoice) -> DELIVERED (DN).
/// Every problem is a mismatch somewhere along this chain.
/// </summary>
public sealed class ChainRow
{
    public required QtyLine Line { get; init; }
    public long Id => Line.Id;
    public string Building => Line.Building;
    public string Level => Line.Level;
    public string Room => Line.Room;
    public string RoomType => Line.RoomType;
    public string System => Line.System;
    public string Stage => Line.Stage;
    public string ItemCode => Line.ItemCode;
    public string Description => Line.Description;
    public string Unit => Line.Unit;
    public double Rate => Line.Rate;

    public double Qs => Line.QsQty;
    public double? ProjectQty => Line.ProjectQty;
    /// <summary>PROJECT QTY caps subcontractor claims; QS is the fallback while PROJECT QTY is not filled.</summary>
    public double Cap => Line.ProjectQty ?? Line.QsQty;
    public double Given { get; set; }
    public double Done { get; set; }
    public double Rework { get; set; }
    public double Claimed { get; set; }
    public double Certified { get; set; }
    public double? Delivered { get; set; }
    public string Subcontractors { get; set; } = "";
    public int OpenWirs { get; set; }
    public int OldestOpenWirDays { get; set; }
    public string LastWirNo { get; set; } = "";
    public string LastInvoiceNo { get; set; } = "";

    public double Remaining => Qs - Given;
    public double SitePct => Line.SitePct;
    public double SiteQty => Rules.ProgressRules.SiteQty(Qs, Line.SitePct);
    public double WirPct => Qs <= 0 ? 0 : Done / Qs;
    public double GivenPct => Qs <= 0 ? 0 : Given / Qs;
    public double ClaimedPct => Qs <= 0 ? 0 : Claimed / Qs;
    public double CertifiableQty => ClaimRules.CertifiableQty(Claimed, Done, Cap);
    public double ClaimedValue => Claimed * Rate;
    public double CertifiedValue => Certified * Rate;
    public double DoneValue => Done * Rate;

    public Verdict Verdict { get; set; }
    public string Status => VerdictText.Of(Verdict);
    public List<Finding> Findings { get; } = new();
    public string Reason => Findings.Count == 0 ? "Chain consistent" : Findings.OrderByDescending(f => f.Severity).First().Message;
    public string Key => $"{Building} {Level} {Room} {System} {Stage}";
}
