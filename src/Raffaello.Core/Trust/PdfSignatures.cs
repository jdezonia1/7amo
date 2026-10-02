using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Writer;

namespace Raffaello.Core.Trust;

/// <summary>Where the visible signature box sits on the approvals page (PDF points, origin bottom-left, A4 portrait).</summary>
public static class ApprovalLayout
{
    public const double PageWidth = 595.28, PageHeight = 841.89;
    /// <summary>x1, y1, x2, y2 of the digital-signature widget (bottom right of the approvals page).</summary>
    public static readonly double[] WidgetRect = { 330, 42, 565, 122 };
}

/// <summary>
/// The "APPROVALS &amp; INTEGRITY" page appended to exported PDFs: one block per signature (purpose, name, role, timestamp,
/// short key fingerprint, verdict), the package SHA-256 and how to verify. Rendered with QuestPDF.
/// </summary>
public static class ApprovalPage
{
    private const string Red = "#8B0000";
    private const string Grey = "#A6A6A6";

    static ApprovalPage() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] Render(string recordTitle, IReadOnlyList<SignatureCheck> checks, string? packageSha256 = null, string? auditSummary = null, DateTime? printedAt = null)
    {
        var at = printedAt ?? DateTime.Now;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(t => t.FontSize(9));
            page.Header().Column(col =>
            {
                col.Item().Text("APPROVALS & INTEGRITY").FontSize(16).Bold().FontColor(Red);
                col.Item().Text(recordTitle).FontSize(11).SemiBold();
                col.Item().PaddingTop(2).LineHorizontal(1).LineColor(Red);
            });
            page.Content().PaddingTop(10).Column(col =>
            {
                if (checks.Count == 0) col.Item().Text("No signatures recorded for this document.").Italic();
                foreach (var c in checks)
                {
                    var s = c.Signature;
                    col.Item().PaddingBottom(8).Border(1).BorderColor(c.Ok ? Grey : Red).Padding(8).Row(row =>
                    {
                        row.RelativeItem(3).Column(b =>
                        {
                            b.Item().Text($"{s.Purpose}  -  {(s.Kind == SignatureKinds.Package ? "INVOICE PACKAGE" : s.Kind == SignatureKinds.Variation ? "VARIATION" : "INVOICE REVISION")}").Bold().FontColor(Red);
                            b.Item().Text($"{(s.SignerName.Length > 0 ? s.SignerName : s.SignerUser)}   ({s.SignerRole})").FontSize(11).SemiBold();
                            b.Item().Text($"Signed {s.SignedAt:dd-MMM-yyyy HH:mm} local  /  {s.SignedAtUtc}");
                            b.Item().Text($"Key fingerprint {KeyFingerprint.Short(s.KeyFingerprint)}   ({s.Algorithm})");
                            if (s.PackageSha256.Length > 0) b.Item().Text($"Package SHA-256 {s.PackageSha256}").FontSize(7);
                            b.Item().Text($"Content SHA-256 {s.PayloadSha256}").FontSize(7);
                        });
                        row.RelativeItem(2).AlignMiddle().Column(b =>
                        {
                            b.Item().AlignRight().Text(c.Verdict).FontSize(13).Bold().FontColor(c.Ok ? "#000000" : Red);
                            b.Item().AlignRight().Text(c.Message).FontSize(8);
                        });
                    });
                }
                if (!string.IsNullOrEmpty(packageSha256))
                    col.Item().PaddingTop(4).Text($"Package file SHA-256: {packageSha256}").FontSize(8);
                if (!string.IsNullOrEmpty(auditSummary))
                    col.Item().PaddingTop(4).Text(auditSummary).FontSize(8);
                col.Item().PaddingTop(10).Text(t =>
                {
                    t.Span("How to verify: ").Bold();
                    t.Span("Raffaello > TRUST > VERIFY, or raffaello-cli verify-signatures / verify-pdf. Each signature is ECDSA P-256 over the record's " +
                           "canonical content and the package SHA-256, made with the signer's personal key (private key protected by Windows DPAPI, " +
                           "public key registered on the Raffaello server). Any change after signing shows as CHANGED.");
                });
                col.Item().PaddingTop(6).Text("These are internal approval signatures (self-issued keys, no external certification authority). " +
                                               "A legally qualified electronic signature needs a certificate issued to MOBCO / the signer by an accredited CA; " +
                                               "Raffaello can apply such a certificate instead (see docs/TRUST.md).").FontSize(7).Italic().FontColor("#555555");
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"Printed {at:dd-MMM-yyyy HH:mm} - Raffaello").FontSize(7).FontColor("#555555");
                r.ConstantItem(240).Height(84).Border(0.5f).BorderColor(Grey).AlignCenter().AlignMiddle()
                    .Text("digital signature").FontSize(7).FontColor(Grey);
            });
        })).GeneratePdf();
    }
}

/// <summary>Page-level PDF operations with PdfPig (copy pages of several PDFs into one, keep everything else).</summary>
public static class PdfPages
{
    /// <summary>All pages of every input, in order.</summary>
    public static byte[] Merge(params byte[][] pdfs)
    {
        var b = new PdfDocumentBuilder();
        var docs = new List<PdfDocument>();
        try
        {
            foreach (var bytes in pdfs)
            {
                var d = PdfDocument.Open(bytes);
                docs.Add(d);
                for (var i = 1; i <= d.NumberOfPages; i++) b.AddPage(d, i);
            }
            return b.Build();
        }
        finally { foreach (var d in docs) d.Dispose(); }
    }

    public static int PageCount(byte[] pdf) { using var d = PdfDocument.Open(pdf); return d.NumberOfPages; }
}

/// <summary>Options of an embedded (PAdES-style) PDF signature.</summary>
public sealed class PdfSignOptions
{
    public string SignerName { get; set; } = "";
    public string Reason { get; set; } = "Approved";
    public string Location { get; set; } = "Riyadh";
    public string ContactInfo { get; set; } = "";
    /// <summary>1-based page of the visible widget; 0 = last page.</summary>
    public int Page { get; set; }
    public double[] Rect { get; set; } = ApprovalLayout.WidgetRect;
    /// <summary>Lines printed in the visible box (ASCII; other characters print as '?').</summary>
    public List<string> AppearanceLines { get; set; } = new();
    public DateTime SigningTimeUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Bytes reserved for the CMS blob (hex doubles it in the file).</summary>
    public int ReservedBytes { get; set; } = 16384;
}

/// <summary>
/// Embeds a CMS (CAdES-detached, PAdES-style) signature in a PDF as an incremental update: signature dictionary, visible
/// widget with an appearance stream, AcroForm, new xref section. The original bytes are untouched (earlier signatures stay
/// valid). By default the certificate is self-issued from the user's Raffaello key - Adobe Reader shows "signed, identity not
/// verified"; with a company certificate (PFX / Windows store) the same code makes a signature Reader trusts.
/// Supports PDFs whose last cross-reference section is a classic table (QuestPDF and PdfPig output).
/// </summary>
public static class PdfCmsSigner
{
    /// <summary>A self-issued certificate for the user's key (identity = Raffaello user; issuer = itself).</summary>
    public static X509Certificate2 SelfIssuedCertificate(ECDsa key, string signerName, string role, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var cn = Regex.Replace(string.IsNullOrWhiteSpace(signerName) ? "Raffaello user" : signerName, @"[,=+<>#;""\\]", " ").Trim();
        var ou = Regex.Replace(string.IsNullOrWhiteSpace(role) ? "Raffaello" : role, @"[,=+<>#;""\\]", " ").Trim();
        var req = new CertificateRequest($"CN={cn}, OU={ou}, O=MOBCO Raffaello (self-issued)", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));
        return req.CreateSelfSigned(new DateTimeOffset(now.AddDays(-1)), new DateTimeOffset(now.AddYears(5)));
    }

    public static byte[] Sign(byte[] pdf, X509Certificate2 certificateWithKey, PdfSignOptions o)
    {
        if (!certificateWithKey.HasPrivateKey) throw new ArgumentException("The certificate has no private key.");
        using var doc = PdfDocument.Open(pdf);
        var st = doc.Structure;
        var tail = Encoding.Latin1.GetString(pdf, (int)Math.Max(0, st.XrefOffset), (int)Math.Min(16, pdf.Length - st.XrefOffset));
        if (!tail.StartsWith("xref", StringComparison.Ordinal))
            throw new NotSupportedException("This PDF uses a cross-reference stream; re-save it through PdfPages.Merge first.");
        var trailer = st.Trailer;
        var rootRef = trailer.Root;
        var catalog = st.Catalog.CatalogDictionary;
        var pages = PageRefs(st, catalog);
        if (pages.Count == 0) throw new InvalidDataException("The PDF has no pages.");
        var pageRef = o.Page <= 0 || o.Page > pages.Count ? pages[^1] : pages[o.Page - 1];
        var pageDict = (DictionaryToken)st.GetObject(pageRef).Data;

        var next = (long)trailer.Size;
        long sigNo = next++, widgetNo = next++, apNo = next++, fontNo = next++;
        var newSize = next;

        // page /Annots += widget
        var annots = new List<IToken>();
        if (pageDict.TryGet(NameToken.Annots, out var a))
        {
            if (a is IndirectReferenceToken ar && st.GetObject(ar.Data).Data is ArrayToken arr) annots.AddRange(arr.Data);
            else if (a is ArrayToken arr2) annots.AddRange(arr2.Data);
        }
        annots.Add(new IndirectReferenceToken(new IndirectReference(widgetNo, 0)));
        var newPage = pageDict.With(NameToken.Annots, new ArrayToken(annots));

        // catalog /AcroForm with the field
        var fields = new List<IToken>();
        if (catalog.TryGet(NameToken.Create("AcroForm"), out var af))
        {
            var afDict = af is IndirectReferenceToken afr ? st.GetObject(afr.Data).Data as DictionaryToken : af as DictionaryToken;
            if (afDict != null && afDict.TryGet(NameToken.Create("Fields"), out var f))
            {
                if (f is IndirectReferenceToken fr && st.GetObject(fr.Data).Data is ArrayToken fa) fields.AddRange(fa.Data);
                else if (f is ArrayToken fa2) fields.AddRange(fa2.Data);
            }
        }
        var fieldName = $"RaffaelloSignature{fields.Count + 1}";
        fields.Add(new IndirectReferenceToken(new IndirectReference(widgetNo, 0)));
        var acro = new DictionaryToken(new Dictionary<NameToken, IToken>
        {
            [NameToken.Create("Fields")] = new ArrayToken(fields),
            [NameToken.Create("SigFlags")] = new NumericToken(3),
        });
        var newCatalog = catalog.With(NameToken.Create("AcroForm"), acro);

        using var ms = new MemoryStream();
        ms.Write(pdf);
        if (pdf.Length > 0 && pdf[^1] != (byte)'\n') ms.WriteByte((byte)'\n');
        var offsets = new SortedDictionary<long, (long Offset, int Gen)>();

        // 1. signature dictionary with placeholders
        offsets[sigNo] = (ms.Position, 0);
        var contentsPlaceholder = new string('0', o.ReservedBytes * 2);
        var byteRangePlaceholder = "[0 0 0 0]" + new string(' ', 36);
        var sigHeader = $"{sigNo} 0 obj\n<</Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached /ByteRange ";
        var sigTail = $" /M ({PdfDate(o.SigningTimeUtc)}) /Name {HexText(o.SignerName)} /Reason {HexText(o.Reason)} /Location {HexText(o.Location)}" +
                      (o.ContactInfo.Length > 0 ? $" /ContactInfo {HexText(o.ContactInfo)}" : "") + ">>\nendobj\n";
        WriteAscii(ms, sigHeader);
        var byteRangePos = ms.Position;
        WriteAscii(ms, byteRangePlaceholder);
        WriteAscii(ms, " /Contents ");
        var contentsStart = ms.Position;   // at '<'
        WriteAscii(ms, "<" + contentsPlaceholder + ">");
        var contentsEnd = ms.Position;     // after '>'
        WriteAscii(ms, sigTail);

        // 2. widget annotation
        offsets[widgetNo] = (ms.Position, 0);
        var r = o.Rect;
        WriteAscii(ms, $"{widgetNo} 0 obj\n<</Type /Annot /Subtype /Widget /FT /Sig /T ({fieldName}) /V {sigNo} 0 R /F 4 " +
                       $"/Rect [{N(r[0])} {N(r[1])} {N(r[2])} {N(r[3])}] /P {pageRef.ObjectNumber} {pageRef.Generation} R /AP <</N {apNo} 0 R>>>>\nendobj\n");

        // 3. appearance stream + font
        var w = r[2] - r[0];
        var h = r[3] - r[1];
        var ap = Appearance(w, h, o.AppearanceLines);
        offsets[apNo] = (ms.Position, 0);
        WriteAscii(ms, $"{apNo} 0 obj\n<</Type /XObject /Subtype /Form /BBox [0 0 {N(w)} {N(h)}] /Resources <</Font <</F1 {fontNo} 0 R>>>> /Length {ap.Length}>>\nstream\n");
        ms.Write(ap);
        WriteAscii(ms, "\nendstream\nendobj\n");
        offsets[fontNo] = (ms.Position, 0);
        WriteAscii(ms, $"{fontNo} 0 obj\n<</Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding>>\nendobj\n");

        // 4. updated page and catalog (same object numbers)
        offsets[pageRef.ObjectNumber] = (ms.Position, pageRef.Generation);
        WriteObject(ms, pageRef.ObjectNumber, pageRef.Generation, newPage);
        offsets[rootRef.ObjectNumber] = (ms.Position, rootRef.Generation);
        WriteObject(ms, rootRef.ObjectNumber, rootRef.Generation, newCatalog);

        // 5. xref + trailer
        var xrefPos = ms.Position;
        var sb = new StringBuilder("xref\n");
        foreach (var run in Runs(offsets.Keys))
        {
            sb.Append(run.First.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(run.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (var n = run.First; n < run.First + run.Count; n++)
            {
                var (off, gen) = offsets[n];
                sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(' ').Append(gen.ToString("D5", CultureInfo.InvariantCulture)).Append(" n\r\n");
            }
        }
        var id0 = trailer.Identifier is { Count: > 0 } ids ? HexOf(ids[0]) : Convert.ToHexString(MD5.HashData(pdf));
        var id1 = Convert.ToHexString(SHA256.HashData(pdf))[..32];
        sb.Append("trailer\n<</Size ").Append(newSize.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root ").Append(rootRef.ObjectNumber.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(rootRef.Generation.ToString(CultureInfo.InvariantCulture)).Append(" R");
        if (trailer.Info is IndirectReferenceToken info)
            sb.Append(" /Info ").Append(info.Data.ObjectNumber.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(info.Data.Generation.ToString(CultureInfo.InvariantCulture)).Append(" R");
        sb.Append(" /Prev ").Append(st.XrefOffset.ToString(CultureInfo.InvariantCulture))
          .Append(" /ID [<").Append(id0).Append("> <").Append(id1).Append(">]>>\nstartxref\n").Append(xrefPos.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        WriteAscii(ms, sb.ToString());

        var bytes = ms.ToArray();
        // 6. byte range = everything except the hex contents (incl. its angle brackets)
        long b1 = contentsStart, b2 = contentsEnd, b3 = bytes.Length - contentsEnd;
        var br = $"[0 {b1} {b2} {b3}]";
        if (br.Length > byteRangePlaceholder.Length) throw new InvalidOperationException("PDF too large for the byte range placeholder.");
        Encoding.ASCII.GetBytes(br.PadRight(byteRangePlaceholder.Length)).CopyTo(bytes, byteRangePos);

        var signed = new byte[b1 + b3];
        Buffer.BlockCopy(bytes, 0, signed, 0, (int)b1);
        Buffer.BlockCopy(bytes, (int)b2, signed, (int)b1, (int)b3);
        var cms = Cms(signed, certificateWithKey);
        if (cms.Length > o.ReservedBytes) throw new InvalidOperationException($"The signature ({cms.Length} bytes) does not fit the reserved {o.ReservedBytes} bytes.");
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(cms));
        hex.CopyTo(bytes, contentsStart + 1);
        return bytes;
    }

    /// <summary>Detached CMS over the signed byte ranges, SHA-256, with the ESS signing-certificate-v2 attribute (CAdES / PAdES baseline).</summary>
    private static byte[] Cms(byte[] content, X509Certificate2 cert)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),   // SHA-256
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", SigningCertificateV2(cert)));
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    private static byte[] SigningCertificateV2(X509Certificate2 cert)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())                 // SigningCertificateV2
        using (w.PushSequence())                 // certs
        using (w.PushSequence())                 // ESSCertIDv2 (hashAlgorithm defaults to SHA-256)
            w.WriteOctetString(SHA256.HashData(cert.RawData));
        return w.Encode();
    }

    private static List<IndirectReference> PageRefs(Structure st, DictionaryToken catalog)
    {
        var list = new List<IndirectReference>();
        if (!catalog.TryGet(NameToken.Pages, out var p) || p is not IndirectReferenceToken root) return list;
        var seen = new HashSet<IndirectReference>();
        void Walk(IndirectReference r, int depth)
        {
            if (depth > 64 || !seen.Add(r)) return;
            if (st.GetObject(r).Data is not DictionaryToken d) return;
            var type = d.TryGet(NameToken.Type, out var t) && t is NameToken nt ? nt.Data : "";
            if (type == "Page" || (!d.ContainsKey(NameToken.Kids) && type != "Pages")) { list.Add(r); return; }
            if (!d.TryGet(NameToken.Kids, out var k)) return;
            var kids = k is ArrayToken ka ? ka : k is IndirectReferenceToken kr ? st.GetObject(kr.Data).Data as ArrayToken : null;
            if (kids is null) return;
            foreach (var kid in kids.Data.OfType<IndirectReferenceToken>()) Walk(kid.Data, depth + 1);
        }
        Walk(root.Data, 0);
        return list;
    }

    private static byte[] Appearance(double w, double h, List<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append("q 1 1 1 rg 0 0 ").Append(N(w)).Append(' ').Append(N(h)).Append(" re f Q\n");
        sb.Append("q 0.545 0 0 RG 1.2 w 0.6 0.6 ").Append(N(w - 1.2)).Append(' ').Append(N(h - 1.2)).Append(" re S Q\n");
        sb.Append("BT 0.545 0 0 rg /F1 8 Tf 5 ").Append(N(h - 11)).Append(" Td (DIGITALLY SIGNED - RAFFAELLO) Tj ET\n");
        var y = h - 22;
        foreach (var line in lines.Take(6))
        {
            if (y < 3) break;
            sb.Append("BT 0 0 0 rg /F1 7 Tf 5 ").Append(N(y)).Append(" Td (").Append(PdfAscii(line)).Append(") Tj ET\n");
            y -= 9;
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string PdfAscii(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s ?? "")
        {
            if (ch is '\\' or '(' or ')') sb.Append('\\').Append(ch);
            else if (ch is >= ' ' and <= '~') sb.Append(ch);
            else sb.Append('?');
        }
        return sb.ToString();
    }

    private static string HexText(string? s)
    {
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(s ?? "")).ToArray();
        return "<" + Convert.ToHexString(bytes) + ">";
    }

    private static string HexOf(IToken t) => t switch
    {
        HexToken hx => Convert.ToHexString(hx.Bytes.ToArray()),
        StringToken s => Convert.ToHexString(s.GetBytes()),
        _ => Convert.ToHexString(Encoding.Latin1.GetBytes(t.ToString() ?? "")),
    };

    private static string PdfDate(DateTime utc) => "D:" + utc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "+00'00'";

    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static void WriteAscii(Stream s, string text) { var b = Encoding.ASCII.GetBytes(text); s.Write(b); }

    private static void WriteObject(Stream s, long number, int gen, IToken token)
    {
        WriteAscii(s, $"{number} {gen} obj\n");
        TokenWriter.Instance.WriteToken(token, s);
        WriteAscii(s, "\nendobj\n");
    }

    private static IEnumerable<(long First, int Count)> Runs(IEnumerable<long> sorted)
    {
        long? start = null; long prev = -2; var count = 0;
        foreach (var n in sorted)
        {
            if (start is null) { start = n; count = 1; }
            else if (n == prev + 1) count++;
            else { yield return (start.Value, count); start = n; count = 1; }
            prev = n;
        }
        if (start is not null) yield return (start.Value, count);
    }
}

/// <summary>One embedded signature found in a PDF.</summary>
public sealed class EmbeddedPdfSignature
{
    public string FieldName { get; set; } = "";
    public string SignerName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Location { get; set; } = "";
    public string SigningTime { get; set; } = "";
    public string CertificateSubject { get; set; } = "";
    public bool SelfIssued { get; set; }
    /// <summary>SHA-256 of the certificate's public key (SPKI) - equals the Raffaello key fingerprint for self-issued signatures.</summary>
    public string KeyFingerprint { get; set; } = "";
    public bool CryptographicallyValid { get; set; }
    /// <summary>The signature covers the file up to its end (nothing was appended after it).</summary>
    public bool CoversWholeFile { get; set; }
    public long[] ByteRange { get; set; } = Array.Empty<long>();
    public string Error { get; set; } = "";
    public bool Ok => CryptographicallyValid && CoversWholeFile;
    public string Summary => Ok
        ? $"VALID - signed by {CertificateSubject} ({Reason}), key {Trust.KeyFingerprint.Short(KeyFingerprint)}{(SelfIssued ? ", self-issued certificate" : "")}; the file has not changed since."
        : !CryptographicallyValid ? $"INVALID - {CertificateSubject}: {Error}" : $"CHANGED - signature by {CertificateSubject} is genuine but the file was modified after signing.";
}

/// <summary>Finds and checks the embedded signatures of a PDF (signature dictionaries with a /ByteRange).</summary>
public static class PdfSignatureVerifier
{
    private static readonly Regex ByteRangeRx = new(@"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]", RegexOptions.Compiled);

    public static List<EmbeddedPdfSignature> Verify(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var list = new List<EmbeddedPdfSignature>();
        foreach (Match m in ByteRangeRx.Matches(text))
        {
            var res = new EmbeddedPdfSignature();
            try
            {
                var br = Enumerable.Range(1, 4).Select(i => long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
                res.ByteRange = br;
                if (br[0] != 0 || br[1] <= 0 || br[2] <= br[1] || br[2] + br[3] > pdf.Length) throw new InvalidDataException("byte range outside the file");
                res.CoversWholeFile = br[2] + br[3] == pdf.Length;
                var hex = text.Substring((int)br[1] + 1, (int)(br[2] - br[1] - 2));
                var blob = Convert.FromHexString(hex);
                var len = DerLength(blob);
                var content = new byte[br[1] + br[3]];
                Buffer.BlockCopy(pdf, 0, content, 0, (int)br[1]);
                Buffer.BlockCopy(pdf, (int)br[2], content, (int)br[1], (int)br[3]);
                var cms = new SignedCms(new ContentInfo(content), detached: true);
                cms.Decode(blob.AsSpan(0, len));
                var si = cms.SignerInfos[0];
                var cert = si.Certificate ?? cms.Certificates.Cast<X509Certificate2>().FirstOrDefault();
                res.CertificateSubject = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? "(no certificate)";
                res.SelfIssued = cert != null && cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData);
                if (cert != null) res.KeyFingerprint = Trust.KeyFingerprint.Of(cert.PublicKey.ExportSubjectPublicKeyInfo());
                si.CheckSignature(verifySignatureOnly: true);
                res.CryptographicallyValid = true;
                var dict = SignatureDictionaryAround(text, m.Index);
                res.SignerName = PdfStringValue(dict, "Name");
                res.Reason = PdfStringValue(dict, "Reason");
                res.Location = PdfStringValue(dict, "Location");
                res.SigningTime = PdfStringValue(dict, "M");
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException or ArgumentException or AsnContentException)
            {
                res.CryptographicallyValid = false;
                res.Error = ex.Message;
            }
            list.Add(res);
        }
        return list;
    }

    private static int DerLength(byte[] blob)
    {
        var r = new AsnReader(blob, AsnEncodingRules.BER);
        return r.PeekEncodedValue().Length;
    }

    private static string SignatureDictionaryAround(string text, int byteRangeIndex)
    {
        var start = text.LastIndexOf("obj", byteRangeIndex, StringComparison.Ordinal);
        var end = text.IndexOf("endobj", byteRangeIndex, StringComparison.Ordinal);
        return start >= 0 && end > start ? text[start..end] : "";
    }

    private static string PdfStringValue(string dict, string key)
    {
        var hex = Regex.Match(dict, $@"/{key}\s*<([0-9A-Fa-f]*)>");
        if (hex.Success)
        {
            var b = Convert.FromHexString(hex.Groups[1].Value);
            return b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2) : Encoding.Latin1.GetString(b);
        }
        var lit = Regex.Match(dict, $@"/{key}\s*\(((?:\\.|[^\\)])*)\)");
        return lit.Success ? Regex.Unescape(lit.Groups[1].Value) : "";
    }
}

/// <summary>Turns an exported PDF into the signed copy: approvals page appended, optionally the embedded digital signature.</summary>
public static class SignedPdfExporter
{
    /// <param name="signWith">The user's key + certificate for the embedded signature; null = approvals page only.</param>
    public static byte[] Produce(byte[] sourcePdf, string recordTitle, IReadOnlyList<SignatureCheck> checks, string? packageSha256, X509Certificate2? signWith,
        PdfSignOptions? options = null, string? auditSummary = null)
    {
        var page = ApprovalPage.Render(recordTitle, checks, packageSha256, auditSummary);
        var merged = PdfPages.Merge(sourcePdf, page);
        if (signWith is null) return merged;
        var o = options ?? new PdfSignOptions();
        o.Page = 0;
        o.Rect = ApprovalLayout.WidgetRect;
        if (o.AppearanceLines.Count == 0)
        {
            var last = checks.LastOrDefault()?.Signature;
            o.AppearanceLines.Add(recordTitle);
            if (last != null)
            {
                o.AppearanceLines.Add($"{last.Purpose} by {(last.SignerName.Length > 0 ? last.SignerName : last.SignerUser)} ({last.SignerRole})");
                o.AppearanceLines.Add($"{last.SignedAt:dd-MMM-yyyy HH:mm}  key {KeyFingerprint.Short(last.KeyFingerprint)}");
            }
            if (!string.IsNullOrEmpty(packageSha256)) o.AppearanceLines.Add("package " + packageSha256[..Math.Min(24, packageSha256.Length)] + "...");
        }
        return PdfCmsSigner.Sign(merged, signWith, o);
    }
}
