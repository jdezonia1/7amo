using CommunityToolkit.Mvvm.ComponentModel;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>Tracker status words (as in the Excel tracker) and the row stripe they map to.</summary>
public static class TrkStatus
{
    public const string Complete = "COMPLETE";
    public const string InProgress = "IN PROGRESS";
    public const string NotStarted = "NOT STARTED";
    public const string OverCap = "OVER CAP";
    public const string NoCap = "NO PROJECT QTY";

    public static string Of(RoomBalance b) =>
        !b.HasCap ? (b.Claimed > LedgerRules.Eps ? NoCap : "-")
        : b.IsOver ? OverCap
        : b.Claimed <= LedgerRules.Eps ? NotStarted
        : b.Remaining <= LedgerRules.Eps ? Complete : InProgress;

    public static string Of(double project, double claimed, bool over) =>
        over ? OverCap : project <= 0 ? (claimed > 0 ? NoCap : "NO DATA") : claimed <= LedgerRules.Eps ? NotStarted : claimed >= project - LedgerRules.Eps ? Complete : InProgress;

    public static string Stripe(string state) => state switch { OverCap => "OVER", NoCap => "CHECK", Complete => "OK", InProgress => "OPEN", _ => "" };

    public static readonly string[] StageOrder = { "CEILING", "1ST FIX", "EMT", "FLEXIBLE", "2ND FIX", "3RD FIX", "DB PANELS", "CABLE PULLING", "CABLE TRAY" };
    public static int StageRank(string s) => Array.IndexOf(StageOrder, (s ?? "").ToUpperInvariant()) is var i && i < 0 ? 99 : i;
}

/// <summary>Display formats of the tracker screens.</summary>
public static class TrkFmt
{
    /// <summary>Shown (yellow badge) where a rate cannot be resolved; the tooltip gives the reason. Excel exports write MISSING.</summary>
    public const string NoRate = "NO RATE";
    /// <summary>Whole numbers stay whole (5, 43, 104); fractions only where they exist (metres, cut lines).</summary>
    public static string Qty(double v) => v.ToString("#,0.##", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>LEDGER tab: one claim line with the tracker columns + rate / amount.</summary>
public sealed class TrkLedgerRow
{
    public required ClaimLine Line { get; init; }
    public required ClaimRate RateInfo { get; init; }
    public required ClaimAmount Amounts { get; init; }
    public RoomBalance? Balance { get; init; }
    public string Sub => Line.Subcontractor;
    public int Invoice => Line.InvoiceNo;
    public string Stage => Line.Stage;
    public string Floor => Line.Floor;
    public string Location => Line.Room;
    public string Item => Line.Item;
    public string Unit => Line.Unit;
    public double Qty => Line.Qty;
    public double SitePct => Line.SitePct;
    public double QtyAfterSite => Line.QtyAfterSite;
    public double WirPct => Line.WirPct;
    public double QtyAfterWir => Line.QtyAfterWir;
    public double? Rate => Amounts.Rate;
    public string RateText => Amounts.Rate is double r ? r.ToString("#,0.##") : TrkFmt.NoRate;
    public string RateTip => RateInfo.IsMissing ? "MISSING: " + RateInfo.Reason : $"{RateInfo.Source}: {RateInfo.Reason}";
    public double Amount => Amounts.AfterWir;
    /// <summary>Amount on screen: empty (not 0) when the rate is missing.</summary>
    public double? AmountShown => RateInfo.IsMissing ? null : Amounts.AfterWir;
    public double? PayableShown => RateInfo.IsMissing ? null : Amounts.Payable;
    public double Payable => Amounts.Payable;
    public double StagePct => RateInfo.StagePct;
    public string RateStatus => RateInfo.Status;
    public string RateSource => RateInfo.Source;
    public string RateReason => RateInfo.Reason;
    public string Notes => Line.Notes;
    public string Rework => Line.Rework ? "YES" : "";
    public string WorkType => Line.WorkType;
    public string Statement => Line.StatementNo;
    public string Source => Line.Source;
    public string EntryCheck => LedgerRules.IsNotCompared(Line) ? "NOT COMPARED" : Line.Rework ? "REWORK" : Line.IsOver ? "OVER" : Balance is null ? "" : !Balance.HasCap ? "NO CAP" : Balance.IsOver ? "KEY OVER" : "OK";
    public double ProjectQty => Balance?.ProjectQty ?? 0;
    public double AllSubs => Balance?.Claimed ?? 0;
    public double Remaining => Balance?.Remaining ?? 0;
    public string Checks => (HeightCheck.IsPending(Line) ? ">4.5 m PENDING " : "") + (LengthCheck.IsPending(Line) ? "15 m PENDING" : "");
    public string Status => Line.IsOver || EntryCheck == "KEY OVER" ? "OVER" : RateInfo.IsMissing ? "CHECK" : Checks.Length > 0 ? "DUE" : "OK";
}

/// <summary>Totals of claim lines per subcontractor / invoice / location / stage.</summary>
public sealed record TrkTotalRow(string Group, int Lines, double Qty, double QtyAfterWir, double Amount, double AfterWir, double Payable, int MissingRate)
{
    public string Status => MissingRate > 0 ? "CHECK" : "OK";
}

/// <summary>ROOMS tab: one location.</summary>
public sealed class TrkRoomRow
{
    public string Location { get; init; } = "";
    public string Part { get; init; } = "";
    public string Floor { get; init; } = "";
    public string Level { get; init; } = "";
    public string Unit { get; init; } = "";
    public string UnitType { get; init; } = "";
    public string Plan { get; init; } = "";
    public double ProjectQty { get; init; }
    public double AllSubs { get; init; }
    public double Remaining { get; init; }
    public double UsedPct => ProjectQty <= 0 ? 0 : AllSubs / ProjectQty;
    public string State { get; init; } = "";
    public string Subs { get; init; } = "";
    public string StagesActive { get; init; } = "";
    public int OverCap { get; init; }
    public int NoCap { get; init; }
    public double Amount { get; init; }
    public string Status => TrkStatus.Stripe(State);
}

/// <summary>ROOM tab A and DASHBOARD: progress per stage.</summary>
public sealed record TrkStageRow(string Stage, double ProjectQty, double SubsQty, double Remaining, string State, int ItemsOver, int ItemsNoCap, int Complete = 0, int InProgress = 0, int NotStarted = 0)
{
    public double UsedPct => ProjectQty <= 0 ? 0 : SubsQty / ProjectQty;
    public string Status => TrkStatus.Stripe(State);
}

/// <summary>ROOM tab C: every stage + item line of one location.</summary>
public sealed record TrkKeyRow(int No, string Stage, string Item, double ProjectQty, double SubsQty, double Remaining, string State, string TopSub, string BySubs, double Amount)
{
    public double UsedPct => ProjectQty <= 0 ? 0 : SubsQty / ProjectQty;
    public string Status => TrkStatus.Stripe(State);
}

/// <summary>PLANS tab: one plan sheet.</summary>
public sealed record TrkPlanRow(string Plan, string Level, int Rooms, double ProjectQty, double Claimed, double Remaining, int OverRooms, int NotStarted, string FirstRoom)
{
    public double UsedPct => ProjectQty <= 0 ? 0 : Claimed / ProjectQty;
    public string Status => OverRooms > 0 ? "OVER" : UsedPct >= 0.999 ? "OK" : Claimed > 0 ? "OPEN" : "";
}

/// <summary>CONTROL tab: one check and its count.</summary>
public sealed record TrkCheckRow(string Code, string Check, int Count)
{
    public string Status => Count == 0 ? "OK" : Code is "OVER" or "UNKNOWN" ? "OVER" : "CHECK";
}

/// <summary>CONTROL tab: a line found by a check.</summary>
public sealed record TrkControlRow(string Location, string Stage, string Item, double ProjectQty, double SubsQty, double Remaining, string Sub, string Invoice, string Note, string Status);

/// <summary>CAP tab.</summary>
public sealed record TrkCapRow(string Key, string Location, string Stage, string Item, double ProjectQty, string Source);

/// <summary>RATES tab: rate of a subcontractor x stage|item (per rate found).</summary>
public sealed record TrkRateRow(string Sub, string Building, string Stage, string Item, string Invoices, int Lines, double Qty, double QtyAfterWir, double? Rate, double StagePct,
    string RateStatus, string Source, string Reason, double Amount, double Payable)
{
    public string RateText => Rate is double r ? r.ToString("#,0.##") : TrkFmt.NoRate;
    public string RateTip => Rate is null ? "MISSING: " + Reason : $"{Source}: {Reason}";
    public string Status => RateStatus == Raffaello.Core.Tracker.RateStatus.Missing ? "CHECK" : RateStatus == Raffaello.Core.Tracker.RateStatus.Check ? "DUE" : "OK";
}

/// <summary>ENTRY: a location to pick.</summary>
public sealed record TrkLocation(string Code, string Caption, string Floor, int FloorRank)
{
    public override string ToString() => Code;
}

/// <summary>ENTRY: one stage x item of the chosen location, with the quantity the subcontractor claims now.</summary>
public sealed partial class TrkEntryRow : ObservableObject
{
    public required string Stage { get; init; }
    public required string Item { get; init; }
    public double Total { get; init; }
    public double Claimed { get; init; }
    public string ByWhom { get; init; } = "";
    public double Remaining { get; init; }
    public string Pending { get; init; } = "";
    public bool NotCompared { get; init; }
    public double? Rate { get; init; }
    public string RateTip { get; init; } = "";
    public string RateText => Rate is double r ? r.ToString("#,0.##") : TrkFmt.NoRate;
    /// <summary>Points as whole numbers (5, 43, 104); metres (cable tray / pulling) with decimals.</summary>
    public string TotalText => TrkFmt.Qty(Total);
    public string ClaimedText => TrkFmt.Qty(Claimed);
    public string RemainingText => TrkFmt.Qty(Remaining);
    [ObservableProperty] private string _claimedNow = "";
    [ObservableProperty] private string _after = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _amount = "";

    /// <summary>Parsed whole number (null = empty or not whole).</summary>
    public int? Qty { get; private set; }
    public bool Invalid { get; private set; }

    partial void OnClaimedNowChanged(string value) => Recalc();

    public void Recalc()
    {
        var t = (ClaimedNow ?? "").Replace(",", "").Trim();
        Invalid = false; Qty = null;
        if (t.Length == 0) { After = ""; Result = ""; Status = ""; Amount = ""; return; }
        if (!int.TryParse(t, out var q) || q < 0) { Invalid = true; Result = "WHOLE NUMBERS ONLY"; Status = "ERROR"; After = ""; Amount = ""; return; }
        Qty = q;
        Amount = Rate is double r ? (q * r).ToString("#,0.##") : TrkFmt.NoRate;
        if (NotCompared) { After = ""; Result = "NOT COMPARED"; Status = "OPEN"; return; }
        var after = Remaining - q;
        After = TrkFmt.Qty(after);
        var whole = Total <= 0 ? 0 : (int)Math.Floor(Math.Max(0, Remaining) + 1e-6);
        if (q <= whole) { Result = "OK"; Status = "OK"; }
        else
        {
            Result = Total <= 0 ? "NO PROJECT QTY" : StatementPlanner.IsSecondFix(Stage) ? $"OVER by {q - whole} - 15 m proof?" : $"OVER by {q - whole}";
            Status = "OVER";
        }
    }
}

/// <summary>ENTRY: one statement line (claimed vs what will be / was certified).</summary>
public sealed partial class TrkStatementRow : ObservableObject
{
    private readonly Action _changed;
    public TrkStatementRow(StatementDraftLine line, Action changed) { Line = line; _changed = changed; _option = line.Option; _reason = line.Reason; }

    public StatementDraftLine Line { get; }
    public string Location => Line.Room;
    public string Stage => Line.Stage;
    public string Item => Line.Item;
    public int Claimed => Line.Claimed;
    [ObservableProperty] private string _option;
    [ObservableProperty] private string _reason;
    [ObservableProperty] private double _remainingBefore;
    [ObservableProperty] private int _certified;
    [ObservableProperty] private int _difference;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _why = "";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _rateTip = "";
    [ObservableProperty] private double _claimedAmount;
    [ObservableProperty] private double _certifiedAmount;
    [ObservableProperty] private double _diffAmount;
    [ObservableProperty] private string _status = "";

    partial void OnOptionChanged(string value) { Line.Option = value; _changed(); }
    partial void OnReasonChanged(string value) { Line.Reason = value ?? ""; _changed(); }

    public void Apply(PlannedLine p, ComparisonRow? c)
    {
        RemainingBefore = p.RemainingBefore;
        Certified = p.Post;
        Difference = p.Claimed - p.Post;
        State = p.Status;
        Why = p.Reason;
        RateText = c?.Rate is double r ? r.ToString("#,0.##") : TrkFmt.NoRate;
        RateTip = c?.RateStatus ?? "";
        ClaimedAmount = c?.ClaimedAmount ?? 0;
        CertifiedAmount = c?.CertifiedAmount ?? 0;
        DiffAmount = c?.DifferenceAmount ?? 0;
        Status = p.Status switch { PlanStatus.Ok => "OK", PlanStatus.Over => "OVER", PlanStatus.NotCompared => "OPEN", PlanStatus.Skipped => "", _ => "CHECK" };
    }
}