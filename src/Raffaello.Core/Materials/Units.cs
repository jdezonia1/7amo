using System.Globalization;

namespace Raffaello.Core.Materials;

/// <summary>Unit normalisation and conversion for supplier documents (KM -> M, PCS <-> M for pipe lengths).</summary>
public static class Units
{
    public const string M = "M";
    public const string Km = "KM";
    public const string Pcs = "PCS";
    public const string Roll = "ROLL";
    public const string Set = "SET";
    public const string Lot = "LOT";
    public const string Kg = "KG";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["M"] = M, ["MT"] = M, ["MTR"] = M, ["MTRS"] = M, ["MTS"] = M, ["METER"] = M, ["METRE"] = M, ["METERS"] = M, ["METRES"] = M,
        ["LM"] = M, ["RM"] = M, ["LIN.M"] = M, ["L.M"] = M, ["M.L"] = M, ["م.ط"] = M, ["م ط"] = M, ["م"] = M,
        ["KM"] = Km, ["KMS"] = Km,
        ["PCS"] = Pcs, ["PC"] = Pcs, ["PCE"] = Pcs, ["NO"] = Pcs, ["NOS"] = Pcs, ["NR"] = Pcs, ["EA"] = Pcs, ["EACH"] = Pcs, ["UNIT"] = Pcs, ["عدد"] = Pcs, ["PT"] = Pcs,
        ["LENGTH"] = Pcs, ["LENGTHS"] = Pcs, ["BAR"] = Pcs, ["BARS"] = Pcs,
        ["ROLL"] = Roll, ["ROLLS"] = Roll, ["COIL"] = Roll, ["COILS"] = Roll, ["DRUM"] = Roll,
        ["SET"] = Set, ["SETS"] = Set, ["LOT"] = Lot, ["LS"] = Lot, ["L.S"] = Lot, ["KG"] = Kg, ["KGS"] = Kg,
    };

    /// <summary>Canonical unit (M, KM, PCS, ROLL, SET, LOT, KG) or the cleaned input when unknown.</summary>
    public static string Normalize(string? unit)
    {
        var u = (unit ?? "").Trim().TrimEnd('.').Trim();
        if (u.Length == 0) return "";
        if (Map.TryGetValue(u, out var c)) return c;
        if (Map.TryGetValue(u.Replace(" ", ""), out c)) return c;
        return u.ToUpperInvariant();
    }

    /// <summary>LENGTH / COUNT / ROLL / OTHER: units that can be compared after conversion.</summary>
    public static string Family(string? unit) => Normalize(unit) switch
    {
        M or Km => "LENGTH",
        Pcs or Set => "COUNT",
        Roll => "ROLL",
        "" => "",
        var x => x,
    };

    /// <summary>Unit agreement for coding: same family, or one side unknown.</summary>
    public static bool Agree(string? a, string? b)
    {
        var fa = Family(a); var fb = Family(b);
        return fa.Length == 0 || fb.Length == 0 || fa == fb;
    }

    public sealed record Conversion(double Qty, string Unit, bool Ok, string Note);

    /// <summary>Converts a quantity to the target (PO) unit. PCS <-> M uses metres per piece (pipe / conduit length).</summary>
    public static Conversion Convert(double qty, string? from, string? to, double metresPerPiece = 0)
    {
        var f = Normalize(from); var t = Normalize(to);
        if (t.Length == 0 || f == t) return new(qty, f.Length == 0 ? t : f, true, "");
        if (f.Length == 0) return new(qty, t, true, "unit missing - assumed " + t);
        if (f == Km && t == M) return new(qty * 1000, M, true, $"{Fmt(qty)} KM x 1000 = {Fmt(qty * 1000)} M");
        if (f == M && t == Km) return new(qty / 1000, Km, true, $"{Fmt(qty)} M / 1000 = {Fmt(qty / 1000)} KM");
        if (f == Pcs && t == M)
            return metresPerPiece > 0 ? new(qty * metresPerPiece, M, true, $"{Fmt(qty)} PCS x {Fmt(metresPerPiece)} M = {Fmt(qty * metresPerPiece)} M")
                                      : new(qty, f, false, "PCS to M needs a length per piece");
        if (f == M && t == Pcs)
            return metresPerPiece > 0 ? new(qty / metresPerPiece, Pcs, true, $"{Fmt(qty)} M / {Fmt(metresPerPiece)} M = {Fmt(qty / metresPerPiece)} PCS")
                                      : new(qty, f, false, "M to PCS needs a length per piece");
        if (Family(f) == Family(t) && Family(f) == "COUNT") return new(qty, t, true, $"{f} counted as {t}");
        return new(qty, f, false, $"cannot convert {f} to {t}");
    }

    public static string Fmt(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "#,##0" : "#,##0.###", CultureInfo.InvariantCulture);

    /// <summary>Parses "1,234.50", "SAR 9,367,585.11", "(12.5)" -> number.</summary>
    public static double? ParseNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Replace("SAR", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Replace(" ", "").Trim();
        var neg = t.StartsWith('(') && t.EndsWith(')');
        t = t.Trim('(', ')');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (neg ? -d : d) : null;
    }
}
