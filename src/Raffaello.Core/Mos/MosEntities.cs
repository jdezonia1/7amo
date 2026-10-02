using Raffaello.Core.Domain;

namespace Raffaello.Core.Mos;

public static class MosStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Rejected = "REJECTED";
    public const string Approved = "APPROVED";
}

/// <summary>An owner materials-on-site valuation (MOS-01 Rev 0, Rev 1 ...). Cumulative; approved revisions are locked.</summary>
public sealed class MosValuation : Entity
{
    public int No { get; set; }
    public int Revision { get; set; }
    public string Status { get; set; } = MosStatus.Draft;
    public DateTime PeriodTo { get; set; }
    public double MosPct { get; set; } = 0.75;
    public string AconexNo { get; set; } = "";
    public string RejectionReason { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Locked { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public string Title => $"MOS-{No:00} Rev {Revision}";
}

/// <summary>One owner BOQ row of a MOS valuation: delivered (DN + MIR) less installed, at BOQ rate x MOS %.</summary>
public sealed class MosLine : Entity
{
    public long ValuationId { get; set; }
    public int RowOrder { get; set; }
    public string BoqCode { get; set; } = "";
    public string BoqDescription { get; set; } = "";
    public string Unit { get; set; } = "";
    public double BoqRate { get; set; }
    public double DeliveredQty { get; set; }
    public double InstalledQty { get; set; }
    public double MosPct { get; set; }
    /// <summary>Cumulative MOS amount of the last approved valuation for this BOQ code.</summary>
    public double PrevAmount { get; set; }
    /// <summary>DN numbers / MIRs behind the delivered quantity.</summary>
    public string Sources { get; set; } = "";
    public string CodeSource { get; set; } = "";

    public double OnSiteQty => Math.Max(0, DeliveredQty - InstalledQty);
    public double CumAmount => Math.Round(OnSiteQty * BoqRate * MosPct, 2);
    public double CurrAmount => Math.Round(CumAmount - PrevAmount, 2);
    /// <summary>Negative current amount = MOS released because the material was installed.</summary>
    public double Released => CurrAmount < 0 ? -CurrAmount : 0;
}

/// <summary>Installed quantity per owner BOQ code (from site progress) - releases MOS.</summary>
public sealed class MosInstalled : Entity
{
    public string BoqCode { get; set; } = "";
    public double Qty { get; set; }
    public DateTime AsOf { get; set; }
    public string Note { get; set; } = "";
}
