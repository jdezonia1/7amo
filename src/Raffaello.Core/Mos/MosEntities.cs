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

/// <summary>
/// One contract BOQ row of a MOS valuation, in the owner's App F layout (03-Oct, Mohamed: "the MOS rule is 75%"):
/// delivered (DN + MIR) less used at site = balance, valued at 75% of the supplier / PO (market) rate, capped at
/// 75% of the BOQ value (BOQ rate x contract qty). Lines saved before the App F fields existed (no market rate) keep
/// the old basis: balance x BOQ rate x MOS %.
/// </summary>
public sealed class MosLine : Entity
{
    public long ValuationId { get; set; }
    public int RowOrder { get; set; }
    /// <summary>Project code of the contract BOQ item.</summary>
    public string BoqCode { get; set; } = "";
    public string BoqDescription { get; set; } = "";
    /// <summary>App F column D: the material delivered (DN / PO description).</summary>
    public string Material { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>App F column E: contract BOQ quantity.</summary>
    public double ContractQty { get; set; }
    public double BoqRate { get; set; }
    /// <summary>Delivered to date (App F column J).</summary>
    public double DeliveredQty { get; set; }
    /// <summary>Delivered up to the previous approved valuation (App F column H).</summary>
    public double PrevDeliveredQty { get; set; }
    /// <summary>Used at site (App F column K).</summary>
    public double InstalledQty { get; set; }
    /// <summary>Supplier / PO unit rate (App F column M), quantity-weighted over the delivered PO lines.</summary>
    public double MarketRate { get; set; }
    public double MosPct { get; set; }
    /// <summary>Cumulative MOS amount of the last approved valuation for this BOQ code.</summary>
    public double PrevAmount { get; set; }
    /// <summary>DN numbers / MIRs behind the delivered quantity.</summary>
    public string Sources { get; set; } = "";
    /// <summary>App F column P: MIR references.</summary>
    public string MirRefs { get; set; } = "";
    /// <summary>App F column Q: PO / subcontract references.</summary>
    public string PoRefs { get; set; } = "";
    public string CodeSource { get; set; } = "";

    public double ThisMonthQty => Math.Round(DeliveredQty - PrevDeliveredQty, 4);
    /// <summary>App F column L: delivered to date less used at site.</summary>
    public double OnSiteQty => Math.Max(0, DeliveredQty - InstalledQty);
    /// <summary>App F column N: MOS % of the market rate.</summary>
    public double MosRate => Math.Round(MarketRate * MosPct, 4);
    /// <summary>75% of the BOQ value - the most MOS can reach for this item (0 = no contract qty, no cap).</summary>
    public double Cap => Math.Round(BoqRate * ContractQty * MosPct, 2);
    public bool UsesMarketRate => MarketRate > 0;
    /// <summary>App F column O.</summary>
    public double CumAmount
    {
        get
        {
            if (!UsesMarketRate) return Math.Round(OnSiteQty * BoqRate * MosPct, 2);
            var qty = InstalledQty > 0 && ContractQty > 0 ? Math.Min(OnSiteQty, ContractQty) : OnSiteQty;
            var amount = Math.Round(qty * MarketRate * MosPct, 2);
            return Cap > 0 ? Math.Min(amount, Cap) : amount;
        }
    }
    public bool Capped => UsesMarketRate && Cap > 0 && Math.Round(OnSiteQty * MarketRate * MosPct, 2) > Cap;
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
