namespace Raffaello.Core.Domain;

public static class Buildings
{
    public const string Hotel = "HOTEL";
    public const string Branded = "BRANDED";
    public static readonly string[] All = { Hotel, Branded };
}

/// <summary>Stages are separate measurements of the same points. They are NEVER summed together.</summary>
public static class Stages
{
    public const string First = "1ST FIX";
    public const string Second = "2ND FIX";
    public const string Final = "FINAL FIX";
    public static readonly string[] All = { First, Second, Final };

    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToUpperInvariant().Replace("-", " ").Replace("_", " ");
        if (s.StartsWith("1") || s.StartsWith("FIRST")) return First;
        if (s.StartsWith("2") || s.StartsWith("SECOND")) return Second;
        if (s.StartsWith("3") || s.StartsWith("FINAL") || s.StartsWith("THIRD")) return Final;
        return "";
    }
}

public static class Systems
{
    public const string Light = "LIGHT";
    public const string Power = "POWER";
    public const string Grms = "GRMS";
    public const string Data = "DATA";
    public const string Av = "AV";
    public const string Disabled = "DISABLED";
    public const string Emergency = "EMERGENCY LIGHT";
    public const string Evacuation = "EVACUATION";
    public const string Emt = "EMT";
    public static readonly string[] Main = { Light, Power, Grms, Data, Av, Disabled, Emergency, Evacuation };
}

public static class WirStatus
{
    public const string Open = "OPEN";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

public static class InvoiceStatus
{
    public const string Received = "RECEIVED";
    public const string Certified = "CERTIFIED";
    public const string Rejected = "REJECTED";
    public const string Redo = "REDO";
}

/// <summary>Row verdicts in order of severity.</summary>
public enum Verdict
{
    Ok = 0,
    Open = 1,
    Due = 2,
    Check = 3,
    Over = 4,
}

public static class VerdictText
{
    public static string Of(Verdict v) => v switch
    {
        Verdict.Over => "OVER",
        Verdict.Check => "CHECK",
        Verdict.Due => "DUE",
        Verdict.Open => "OPEN",
        _ => "OK",
    };
}
