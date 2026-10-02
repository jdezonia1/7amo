using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Trust;
using UglyToad.PdfPig;

namespace Raffaello.Core.Tests;

/// <summary>[trust] Approvals page, merged signed copy and the embedded (PAdES-style) PDF signature.</summary>
public sealed class TrustPdfTests
{
    private static byte[] SamplePdf(int pages = 2)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c =>
        {
            for (var i = 1; i <= pages; i++)
            {
                var n = i;
                c.Page(p => { p.Size(PageSizes.A4); p.Content().Text($"Invoice page {n}"); });
            }
        }).GeneratePdf();
    }

    private static (SignatureCheck Check, System.Security.Cryptography.ECDsa Key, SigningKeyInfo Info) Signed()
    {
        var keys = TrustSignatureTests.Keys();
        var info = keys.GetOrCreate("reviewer");
        var sig = SignatureService.Sign(keys, new SignerInfo("reviewer", "Head Office Reviewer", "REVIEWER"), SignatureKinds.Package, "SubInvoices", 7,
            "SUB-ELE-001 SUB-A INV-01 Rev 0", SignaturePurposes.Approved, new string('a', 64), new string('b', 64));
        var reg = new UserSigningKey { UserName = "reviewer", Fingerprint = info.Fingerprint, PublicKey = info.PublicKey };
        var check = SignatureService.Verify(sig, new string('a', 64), new string('b', 64), new[] { reg });
        return (check, keys.OpenPrivateKey("reviewer"), info);
    }

    [Fact]
    public void Signed_copy_has_the_approvals_page_and_a_valid_embedded_signature()
    {
        var (check, key, info) = Signed();
        Assert.True(check.Ok);
        using var cert = PdfCmsSigner.SelfIssuedCertificate(key, "Head Office Reviewer", "REVIEWER");
        var pdf = SignedPdfExporter.Produce(SamplePdf(), "SUB-ELE-001 SUB-A INV-01 Rev 0", new[] { check }, new string('b', 64), cert);

        using (var doc = PdfDocument.Open(pdf))
        {
            Assert.Equal(3, doc.NumberOfPages);
            var last = doc.GetPage(3).Text;
            Assert.Contains("APPROVALS & INTEGRITY", last);
            Assert.Contains("Head Office Reviewer", last);
            Assert.Contains(KeyFingerprint.Short(info.Fingerprint), last);
            Assert.Contains("VALID", last);
            Assert.Contains("Invoice page 1", doc.GetPage(1).Text);
            Assert.Single(doc.GetPage(3).GetAnnotations());
        }

        var sigs = PdfSignatureVerifier.Verify(pdf);
        var s = Assert.Single(sigs);
        Assert.True(s.Ok, s.Error);
        Assert.True(s.SelfIssued);
        Assert.Equal(info.Fingerprint, s.KeyFingerprint);   // the PDF signature is tied to the registered Raffaello key
        Assert.Equal("Head Office Reviewer", s.CertificateSubject);
        Assert.Contains("VALID", s.Summary);
    }

    [Fact]
    public void Any_byte_changed_after_signing_breaks_the_embedded_signature()
    {
        var (check, key, _) = Signed();
        using var cert = PdfCmsSigner.SelfIssuedCertificate(key, "Reviewer", "REVIEWER");
        var pdf = SignedPdfExporter.Produce(SamplePdf(1), "T", new[] { check }, null, cert);
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var at = text.IndexOf("Invoice page 1", StringComparison.Ordinal);
        if (at < 0) at = 200;   // content is compressed: flip any byte inside the signed range
        pdf[at] ^= 0x01;
        var s = Assert.Single(PdfSignatureVerifier.Verify(pdf));
        Assert.False(s.CryptographicallyValid);

        // appending an incremental update after the signature: still genuine, but no longer the whole file
        var clean = SignedPdfExporter.Produce(SamplePdf(1), "T", new[] { check }, null, cert);
        var appended = clean.Concat(System.Text.Encoding.ASCII.GetBytes("\n% later change\n")).ToArray();
        var s2 = Assert.Single(PdfSignatureVerifier.Verify(appended));
        Assert.True(s2.CryptographicallyValid);
        Assert.False(s2.CoversWholeFile);
        Assert.Contains("CHANGED", s2.Summary);
    }

    [Fact]
    public void A_second_signature_keeps_the_first_one_valid()
    {
        var (check, key, _) = Signed();
        using var cert = PdfCmsSigner.SelfIssuedCertificate(key, "Reviewer", "REVIEWER");
        var once = SignedPdfExporter.Produce(SamplePdf(1), "T", new[] { check }, null, cert);
        var twice = PdfCmsSigner.Sign(once, cert, new PdfSignOptions { SignerName = "Second", Reason = "Checked", Page = 1, Rect = new double[] { 40, 40, 200, 100 } });
        var sigs = PdfSignatureVerifier.Verify(twice);
        Assert.Equal(2, sigs.Count);
        Assert.All(sigs, s => Assert.True(s.CryptographicallyValid, s.Error));
        Assert.False(sigs[0].CoversWholeFile);   // the first one covers the file as it was
        Assert.True(sigs[1].CoversWholeFile);
        using var doc = PdfDocument.Open(twice);
        Assert.Equal(2, doc.NumberOfPages);
    }

    [Fact]
    public void Unsigned_copy_only_appends_the_page()
    {
        var (check, _, _) = Signed();
        var pdf = SignedPdfExporter.Produce(SamplePdf(2), "T", new[] { check }, null, null);
        Assert.Empty(PdfSignatureVerifier.Verify(pdf));
        Assert.Equal(3, PdfPages.PageCount(pdf));
    }
}
