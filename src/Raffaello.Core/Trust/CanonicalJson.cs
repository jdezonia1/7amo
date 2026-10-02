using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raffaello.Core.Trust;

/// <summary>
/// Deterministic JSON for signing: object properties sorted by ordinal name, no whitespace, numbers in shortest round-trip
/// form, dates to the second (both databases keep at least that, so a record re-read from SQLite or PostgreSQL gives the same
/// text), enums as numbers, null kept. Only public read/write properties are written (computed getters such as Amount are not
/// stored and are left out), minus the excluded names.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(object? value, ISet<string>? exclude = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(w, value, exclude ?? new HashSet<string>(), 0);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Sha256(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static void Write(Utf8JsonWriter w, object? v, ISet<string> exclude, int depth)
    {
        if (depth > 16) throw new InvalidOperationException("Object too deep for canonical JSON.");
        switch (v)
        {
            case null: w.WriteNullValue(); return;
            case string s: w.WriteStringValue(s); return;
            case bool b: w.WriteBooleanValue(b); return;
            case DateTime dt: w.WriteStringValue(dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)); return;
            case DateTimeOffset dto: w.WriteStringValue(dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)); return;
            case Guid g: w.WriteStringValue(g.ToString("D")); return;
            case Enum e: w.WriteNumberValue(Convert.ToInt64(e, CultureInfo.InvariantCulture)); return;
            case double d: Number(w, d); return;
            case float f: Number(w, f); return;
            case decimal m: w.WriteRawValue(m.ToString(CultureInfo.InvariantCulture), skipInputValidation: true); return;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                w.WriteRawValue(Convert.ToString(v, CultureInfo.InvariantCulture)!, skipInputValidation: true); return;
            case byte[] bytes: w.WriteStringValue(Convert.ToBase64String(bytes)); return;
            case IDictionary dict:
            {
                w.WriteStartObject();
                foreach (var k in dict.Keys.Cast<object>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? "").OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (exclude.Contains(k)) continue;
                    w.WritePropertyName(k);
                    Write(w, FindKey(dict, k), exclude, depth + 1);
                }
                w.WriteEndObject();
                return;
            }
            case IEnumerable list:
                w.WriteStartArray();
                foreach (var item in list) Write(w, item, exclude, depth + 1);
                w.WriteEndArray();
                return;
        }
        w.WriteStartObject();
        foreach (var p in Props(v.GetType()))
        {
            if (exclude.Contains(p.Name)) continue;
            w.WritePropertyName(p.Name);
            Write(w, p.GetValue(v), exclude, depth + 1);
        }
        w.WriteEndObject();
    }

    private static object? FindKey(IDictionary dict, string key)
    {
        foreach (DictionaryEntry e in dict)
            if (Convert.ToString(e.Key, CultureInfo.InvariantCulture) == key) return e.Value;
        return null;
    }

    private static void Number(Utf8JsonWriter w, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) { w.WriteStringValue(d.ToString(CultureInfo.InvariantCulture)); return; }
        if (d == 0) d = 0;   // -0 and 0 are the same quantity
        w.WriteRawValue(d.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
    }

    private static IEnumerable<PropertyInfo> Props(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);
}
