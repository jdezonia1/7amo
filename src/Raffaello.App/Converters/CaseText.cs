using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using Raffaello.App.Resources;

namespace Raffaello.App.Converters;

/// <summary>
/// Calm, professional text: ALL-CAPS titles from the view models ("ROOMS &amp; LEDGER") are shown in sentence case ("Rooms &amp; ledger");
/// trade acronyms (WIR, MIR, BOQ, MOS, S/C, GRMS ...) stay as they are. Arabic and mixed-case text is left untouched.
/// </summary>
public static class CaseText
{
    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "WIR", "MIR", "BOQ", "PO", "POS", "DN", "DNS", "MOS", "IPC", "SAR", "QTY", "ID", "OK", "PDF", "DWG", "DXF", "IFC", "CSV", "AV", "BMS", "GRMS", "CCTV", "DALI",
        "EMT", "PVC", "RS", "SLD", "UI", "API", "EI", "S/C", "QS", "TV", "IT", "HOTEL", "BRANDED", "ACONEX", "RAFFAELLO", "MEP", "LED", "DB", "KPI", "AI", "OCR",
        "CTRL", "VAT", "RFI", "NCR", "PQ", "WIR/MIR", "FROM-TO", "E-PROMISE", "B5", "B6", "MOBCO", "RAFFLES", "CAD", "EV",
    };
    private static readonly Regex Ordinal = new(@"^\d+[SNRT][TDH]$", RegexOptions.Compiled);

    public static string Sentence(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length < 3 || letters.Any(c => c > 127) || letters.Any(char.IsLower)) return text;
        var parts = Regex.Split(text, @"(\s+)");
        var first = true;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (string.IsNullOrWhiteSpace(p)) continue;
            var core = Regex.Replace(p, @"[^A-Z0-9/&\-]", "");
            if (Acronyms.Contains(core)) { }
            else if (Ordinal.IsMatch(core)) parts[i] = p.ToLowerInvariant();
            else
            {
                var lw = p.ToLowerInvariant();
                parts[i] = first && lw.Length > 0 ? char.ToUpperInvariant(lw[0]) + lw[1..] : lw;
            }
            first = p is "|" or "-" or ">" or "/" || p.EndsWith('.') || p.EndsWith(':');
        }
        return string.Concat(parts);
    }
}

/// <summary>Translation (TrConverter) then sentence case - page titles in the header.</summary>
public sealed class TrSentenceConverter : IMultiValueConverter, IValueConverter
{
    public static TrSentenceConverter Instance { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => CaseText.Sentence(TrConverter.Instance.Convert(values, targetType, parameter, culture) as string);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => CaseText.Sentence(TrConverter.Tr(value as string ?? value?.ToString()));
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => Array.Empty<object>();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}