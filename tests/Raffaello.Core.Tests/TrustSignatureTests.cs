using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Trust;

namespace Raffaello.Core.Tests;

/// <summary>[trust] Per-user signing keys, sign-off of invoice revisions / packages, verification.</summary>
public sealed class TrustSignatureTests
{
    internal static SigningKeyStore Keys(string? folder = null) => new(folder ?? TestData.TempDir(), new PassphraseKeyProtector("test-passphrase") { Iterations = 1000 });

    internal static SubInvoice Invoice(IProjectStore db)
    {
        var inv = db.Insert(new SubInvoice { Subcontractor = "SUB-A", ContractNo = "SUB-ELE-001", InvoiceNo = 1, CreatedAt = new DateTime(2026, 9, 30, 10, 0, 0) });
        db.InsertMany(new[]
        {
            new SubInvoiceLine { SubInvoiceId = inv.Id, RowOrder = 1, ItemNo = "1", BoqCode = "B6-01-01-00-1", Description = "Lighting point", Unit = "No", ContractQty = 100, Rate = 55, StagePct = 0.9, CurrQty = 40, CumQty = 40 },
            new SubInvoiceLine { SubInvoiceId = inv.Id, RowOrder = 2, ItemNo = "2", BoqCode = "B6-01-01-00-2", Description = "Power point", Unit = "No", ContractQty = 80, Rate = 60, StagePct = 0.9, CurrQty = 20.5, CumQty = 20.5 },
        });
        return inv;
    }

    [Fact]
    public void Key_is_generated_once_protected_and_opens_again()
    {
        var folder = TestData.TempDir();
        var ks = Keys(folder);
        var k1 = ks.GetOrCreate("mohamed");
        var k2 = ks.GetOrCreate("Mohamed");
        Assert.Equal(k1.Fingerprint, k2.Fingerprint);
        Assert.Equal(64, k1.Fingerprint.Length);
        var file = Directory.GetFiles(folder).Single();
        Assert.DoesNotContain("PRIVATE", File.ReadAllText(file));
        using var priv = ks.OpenPrivateKey("mohamed");
        Assert.Equal(k1.Fingerprint, KeyFingerprint.Of(priv.ExportSubjectPublicKeyInfo()));
        var wrong = new SigningKeyStore(folder, new PassphraseKeyProtector("another") { Iterations = 1000 });
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => wrong.OpenPrivateKey("mohamed"));
    }

    [Fact]
    public void Signed_invoice_verifies_until_its_content_changes()
    {
        var db = TestData.NewDb();
        var inv = Invoice(db);
        var trust = new SqliteTrustStore(db);
        var svc = new TrustService(db, trust, Keys());
        var me = new SignerInfo("tester", "Test Engineer", "QS");

        var sig = svc.SignInvoice(inv, me, SignaturePurposes.Approved);
        Assert.Equal(SignatureKinds.Invoice, sig.Kind);
        var check = Assert.Single(svc.Verify(SignedRecords.InvoiceTable, inv.Id));
        Assert.Equal(SignatureVerdicts.Valid, check.Verdict);
        Assert.Contains("approved by Test Engineer (QS)", check.Message);
        Assert.Contains("has not changed", check.Message);

        // status / Aconex no. are workflow fields: submitting does not "change" the approved content
        var h = db.Get<SubInvoice>(inv.Id)!;
        h.Status = SubInvoiceStatus.Submitted; h.AconexWorkflowNo = "WF-000123"; h.SubmittedAt = DateTime.Now;
        db.Update(h);
        Assert.True(svc.Verify(SignedRecords.InvoiceTable, inv.Id).Single().Ok);

        // a quantity change does
        var line = db.All<SubInvoiceLine>().First(l => l.SubInvoiceId == inv.Id);
        line.CurrQty += 1;
        db.Update(line);
        var after = svc.Verify(SignedRecords.InvoiceTable, inv.Id).Single();
        Assert.Equal(SignatureVerdicts.Changed, after.Verdict);
        Assert.True(after.SignatureValid);
        Assert.False(after.ContentUnchanged);
    }

    [Fact]
    public void Forged_or_edited_signature_rows_are_rejected()
    {
        var db = TestData.NewDb();
        var inv = Invoice(db);
        var trust = new SqliteTrustStore(db);
        var keys = Keys();
        var svc = new TrustService(db, trust, keys);
        var me = new SignerInfo("tester", "Test Engineer", "QS");
        svc.SignInvoice(inv, me, SignaturePurposes.Checked);

        // someone edits the purpose in the data file: statement no longer matches the row
        var s = trust.Signatures().Single();
        s.Purpose = SignaturePurposes.Approved;
        trust.Update(s);
        Assert.Equal(SignatureVerdicts.Invalid, svc.Verify(SignedRecords.InvoiceTable, inv.Id).Single().Verdict);

        // a signature made with a key that is not registered to the signer
        var otherKeys = Keys();
        var payload = TrustService.CurrentPayloadSha(db, SignedRecords.InvoiceTable, inv.Id)!;
        var forged = SignatureService.Sign(otherKeys, new SignerInfo("tester", "Test Engineer", "QS"), SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, "x", SignaturePurposes.Approved, payload);
        var ex = Assert.Throws<InvalidOperationException>(() => trust.AddSignature(forged));
        Assert.Contains("not registered", ex.Message);

        // signing in someone else's name is refused
        var asOther = SignatureService.Sign(keys, new SignerInfo("boss", "Boss", "REVIEWER"), SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, "x", SignaturePurposes.Approved, payload);
        Assert.Contains("only sign as themself", Assert.Throws<InvalidOperationException>(() => trust.AddSignature(asOther)).Message);
    }

    [Fact]
    public void Package_signature_covers_the_zip_bytes()
    {
        var db = TestData.NewDb();
        var inv = Invoice(db);
        var zip = Path.Combine(TestData.TempDir(), "SUB-ELE-001_SUB-A_INV-01_Rev0.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3, 4, 5 });
        var h = db.Get<SubInvoice>(inv.Id)!;
        h.PackageFile = zip; h.PackageSha256 = SignatureService.FileSha256(zip); h.PackageKind = "FINAL";
        db.Update(h);
        var trust = new SqliteTrustStore(db);
        var svc = new TrustService(db, trust, Keys());
        var sig = svc.SignPackage(h, new SignerInfo("tester", "Test Engineer", "REVIEWER"), SignaturePurposes.Approved);
        Assert.Equal(h.PackageSha256, sig.PackageSha256);
        Assert.True(svc.Verify(SignedRecords.InvoiceTable, inv.Id).Single().Ok);
        Assert.True(svc.VerifyPackageFile(zip).Single().Ok);

        File.WriteAllBytes(zip, new byte[] { 1, 2, 3, 4, 6 });   // the ZIP is swapped
        var c = svc.Verify(SignedRecords.InvoiceTable, inv.Id).Single();
        Assert.Equal(SignatureVerdicts.Changed, c.Verdict);
        Assert.False(c.PackageUnchanged);
        Assert.Empty(svc.VerifyPackageFile(zip));   // no signature covers these bytes
    }

    [Fact]
    public void New_key_withdraws_the_old_one_but_old_signatures_stay_valid()
    {
        var db = TestData.NewDb();
        var inv = Invoice(db);
        var trust = new SqliteTrustStore(db);
        var folder = TestData.TempDir();
        var keys = Keys(folder);
        var svc = new TrustService(db, trust, keys);
        var me = new SignerInfo("tester", "Test Engineer", "QS");
        svc.SignInvoice(inv, me, SignaturePurposes.Prepared);
        keys.Create("tester");                      // lost laptop: new key
        svc.SignInvoice(inv, me, SignaturePurposes.Checked);
        Assert.Equal(2, trust.Keys().Count);
        Assert.Single(trust.Keys(), k => k.RevokedAt is null);
        Assert.All(svc.Verify(SignedRecords.InvoiceTable, inv.Id), c => Assert.True(c.Ok, c.Message));
    }

    [Fact]
    public void Canonical_json_is_stable_and_ignores_row_stamps()
    {
        var a = new SubInvoiceLine { Id = 5, Description = "x", CurrQty = 0.1 + 0.2, UpdatedBy = "a", RowVersion = 1 };
        var b = new SubInvoiceLine { Id = 5, Description = "x", CurrQty = 0.1 + 0.2, UpdatedBy = "b", RowVersion = 9, UpdatedAt = DateTime.Now };
        Assert.Equal(CanonicalJson.Serialize(a, SignedRecords.RowStamps), CanonicalJson.Serialize(b, SignedRecords.RowStamps));
        var json = CanonicalJson.Serialize(a, SignedRecords.RowStamps);
        Assert.Contains("\"CurrQty\":0.30000000000000004", json);
        Assert.True(json.IndexOf("\"BoqCode\"", StringComparison.Ordinal) < json.IndexOf("\"CurrQty\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Amount", json);   // computed getter, not stored
    }

    [Fact]
    public void Statement_round_trips()
    {
        var st = new SignatureStatement
        {
            Kind = SignatureKinds.Package, RecordTable = "SubInvoices", RecordId = 12, Title = "X INV-01\nRev 0", Purpose = SignaturePurposes.Approved,
            PayloadSha256 = "ab", PackageSha256 = "cd", Signer = "u", SignerName = "U", SignerRole = "QS", KeyFingerprint = "ef", SignedAtUtc = "2026-10-02T07:00:00Z",
        };
        var p = SignatureStatement.Parse(st.Text());
        Assert.Equal(12, p.RecordId);
        Assert.Equal("X INV-01 Rev 0", p.Title);   // new lines cannot be smuggled in
        Assert.Equal("cd", p.PackageSha256);
    }
}
