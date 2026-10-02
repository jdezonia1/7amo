using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Integrations.AconexApi;
using Raffaello.Core.Integrations.CadExchange;
using Raffaello.Core.Integrations.EPromise;
using Raffaello.Core.Remote;
using Raffaello.Core.Trust;

namespace Raffaello.Cli;

/// <summary>
/// [trust] Integrity, signature and integration commands:
///   raffaello-cli verify-audit      [--db FILE] | --server URL --token T [--independent]      exit 0 intact, 3 tampered
///   raffaello-cli verify-signatures --db FILE (--invoice-id N | --contract NO --sub NAME --invoice N [--revision R]) [--package ZIP]
///   raffaello-cli verify-package    ZIP --db FILE                     which approvals cover exactly these bytes
///   raffaello-cli sign              --db FILE --invoice-id N --purpose PREPARED|CHECKED|APPROVED [--kind INVOICE|PACKAGE]
///                                   --user U [--name "Display Name"] [--role QS] [--passphrase P (no DPAPI)]
///   raffaello-cli signed-pdf        PDF --db FILE --invoice-id N --out FILE [--sign --user U [--passphrase P]]
///   raffaello-cli verify-pdf        PDF
///   raffaello-cli epromise-export   --db FILE --invoice-id N --out FILE.xlsx|.csv [--config epromise-export.json] [--force]
///   raffaello-cli cad-import        FILE.json --db FILE [--building B] [--stages "1ST FIX,2ND FIX"] [--mode REPLACE|ADD_MISSING] [--commit]
///   raffaello-cli cad-schema        [--out FILE]
///   raffaello-cli aconex-api-test   [--config aconex-api.json]
/// </summary>
public static class TrustCommands
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "verify-audit", "verify-signatures", "verify-package", "sign", "signed-pdf", "verify-pdf", "epromise-export", "cad-import", "cad-schema", "aconex-api-test",
    };

    public static bool Handles(string cmd) => Names.Contains(cmd);

    public static int Run(string[] args)
    {
        var o = Options(args.Skip(1).ToArray(), out var pos);
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "verify-audit": return VerifyAudit(o);
                case "verify-signatures": return VerifySignatures(o);
                case "verify-package": return VerifyPackage(pos[0], o);
                case "sign": return Sign(o);
                case "signed-pdf": return SignedPdf(pos[0], o);
                case "verify-pdf": return VerifyPdf(pos[0]);
                case "epromise-export": return EPromise(o);
                case "cad-import": return CadImport(pos[0], o);
                case "cad-schema":
                    var schema = CadExchangeImporter.JsonSchema();
                    if (o.TryGetValue("out", out var outPath)) { File.WriteAllText(outPath, schema); Console.WriteLine($"Schema written to {outPath}"); }
                    else Console.WriteLine(schema);
                    return 0;
                case "aconex-api-test": return AconexApiTest(o);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 1;
        }
        return 2;
    }

    private static Dictionary<string, string> Options(string[] a, out List<string> positional)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--", StringComparison.Ordinal))
            {
                var k = a[i][2..];
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal)) d[k] = a[++i]; else d[k] = "true";
            }
            else positional.Add(a[i]);
        }
        return d;
    }

    private static string Req(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");

    private static Db OpenDb(Dictionary<string, string> o)
    {
        var path = Req(o, "db");
        if (!File.Exists(path)) throw new FileNotFoundException($"No data file {path}");
        var db = new Db(path, o.GetValueOrDefault("user", "cli"));
        db.EnsureSchema();
        return db;
    }

    private static SigningKeyStore Keys(Dictionary<string, string> o) => SigningKeyStore.Default(o.GetValueOrDefault("passphrase"));

    private static SubInvoice FindInvoice(IProjectStore db, Dictionary<string, string> o)
    {
        var all = db.All<SubInvoice>();
        if (o.TryGetValue("invoice-id", out var id)) return all.FirstOrDefault(i => i.Id == long.Parse(id)) ?? throw new ArgumentException($"No invoice #{id}");
        var contract = Req(o, "contract"); var sub = Req(o, "sub"); var no = int.Parse(Req(o, "invoice"));
        var list = all.Where(i => i.ContractNo.Equals(contract, StringComparison.OrdinalIgnoreCase) && i.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase) && i.InvoiceNo == no).ToList();
        if (o.TryGetValue("revision", out var rev)) list = list.Where(i => i.Revision == int.Parse(rev)).ToList();
        return list.OrderByDescending(i => i.Revision).FirstOrDefault() ?? throw new ArgumentException($"No invoice {contract} {sub} INV-{no:00}");
    }

    private static int VerifyAudit(Dictionary<string, string> o)
    {
        var checker = new IntegrityChecker();
        AuditVerifyReport rep;
        if (o.TryGetValue("server", out var url))
        {
            using var api = new RemoteApi(url, Req(o, "token"), false, Environment.MachineName, "cli", TimeSpan.FromMinutes(10));
            var anchors = new AuditAnchorStore();
            rep = o.ContainsKey("independent") ? IntegrityChecker.VerifyIndependently(api, anchors.For(url), new Progress<long>(n => Console.Error.Write($"\r{n:N0} rows"))) : IntegrityChecker.VerifyOnServer(api, anchors.For(url));
            anchors.Remember(url, rep);
        }
        else rep = checker.Verify(OpenDb(o));
        Console.WriteLine(rep.ToText());
        return rep.Ok ? 0 : 3;
    }

    private static int VerifySignatures(Dictionary<string, string> o)
    {
        var db = OpenDb(o);
        var inv = FindInvoice(db, o);
        var svc = new TrustService(db, new SqliteTrustStore(db), new SigningKeyStore(SigningKeyStore.DefaultFolder, new PassphraseKeyProtector("unused")));
        var checks = svc.Verify(SignedRecords.InvoiceTable, inv.Id, o.GetValueOrDefault("package"));
        Console.WriteLine($"{SignedRecords.InvoiceTitle(inv)}: {checks.Count} signature(s)");
        foreach (var c in checks) Console.WriteLine($"  {c.Verdict,-12} {c.Signature.Purpose,-9} {c.Signature.SignerUser,-14} {c.Signature.SignedAtUtc}  {c.Message}");
        return checks.All(c => c.Ok) ? 0 : 3;
    }

    private static int VerifyPackage(string zip, Dictionary<string, string> o)
    {
        var db = OpenDb(o);
        var svc = new TrustService(db, new SqliteTrustStore(db), new SigningKeyStore(SigningKeyStore.DefaultFolder, new PassphraseKeyProtector("unused")));
        var checks = svc.VerifyPackageFile(zip);
        Console.WriteLine($"{Path.GetFileName(zip)} sha256 {SignatureService.FileSha256(zip)}");
        if (checks.Count == 0) { Console.WriteLine("  NO signature covers these exact bytes."); return 3; }
        foreach (var c in checks) Console.WriteLine($"  {c.Verdict,-12} {c.Message}");
        return checks.All(c => c.Ok) ? 0 : 3;
    }

    private static int Sign(Dictionary<string, string> o)
    {
        var db = OpenDb(o);
        var inv = FindInvoice(db, o);
        var signer = new SignerInfo(Req(o, "user"), o.GetValueOrDefault("name", Req(o, "user")), o.GetValueOrDefault("role", "QS"));
        db.User = signer.User;
        var svc = new TrustService(db, new SqliteTrustStore(db), Keys(o));
        var purpose = Req(o, "purpose").ToUpperInvariant();
        var sig = o.GetValueOrDefault("kind", "INVOICE").Equals("PACKAGE", StringComparison.OrdinalIgnoreCase) ? svc.SignPackage(inv, signer, purpose) : svc.SignInvoice(inv, signer, purpose);
        Console.WriteLine($"Signed {sig.RecordTitle} as {sig.Purpose} by {sig.SignerUser}, key {KeyFingerprint.Short(sig.KeyFingerprint)}, {sig.SignedAtUtc}");
        return 0;
    }

    private static int SignedPdf(string pdf, Dictionary<string, string> o)
    {
        var db = OpenDb(o);
        var inv = FindInvoice(db, o);
        var keys = o.ContainsKey("sign") ? Keys(o) : new SigningKeyStore(SigningKeyStore.DefaultFolder, new PassphraseKeyProtector("unused"));
        var svc = new TrustService(db, new SqliteTrustStore(db), keys);
        var checks = svc.Verify(SignedRecords.InvoiceTable, inv.Id);
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
        if (o.ContainsKey("sign"))
        {
            var user = Req(o, "user");
            using var k = keys.OpenPrivateKey(user);
            cert = PdfCmsSigner.SelfIssuedCertificate(k, o.GetValueOrDefault("name", user), o.GetValueOrDefault("role", "QS"));
        }
        var audit = SqliteAuditChain.Verify(db.Path).Summary;
        var bytes = SignedPdfExporter.Produce(File.ReadAllBytes(pdf), SignedRecords.InvoiceTitle(inv), checks, inv.PackageSha256, cert, new PdfSignOptions { SignerName = o.GetValueOrDefault("name", o.GetValueOrDefault("user", "")), Reason = "Raffaello approval" }, audit);
        File.WriteAllBytes(Req(o, "out"), bytes);
        cert?.Dispose();
        Console.WriteLine($"Signed copy written: {o["out"]} ({checks.Count} approval(s){(cert != null ? ", embedded digital signature" : "")})");
        return 0;
    }

    private static int VerifyPdf(string pdf)
    {
        var sigs = PdfSignatureVerifier.Verify(File.ReadAllBytes(pdf));
        if (sigs.Count == 0) { Console.WriteLine("No embedded signature."); return 3; }
        foreach (var s in sigs) Console.WriteLine($"  {s.Summary}");
        return sigs[^1].Ok && sigs.All(s => s.CryptographicallyValid) ? 0 : 3;
    }

    private static int EPromise(Dictionary<string, string> o)
    {
        var db = OpenDb(o);
        var inv = FindInvoice(db, o);
        var cfg = o.TryGetValue("config", out var c) ? EPromiseExportConfig.Load(c, writeIfMissing: false) : EPromiseExportConfig.Load(writeIfMissing: false);
        var problems = cfg.Validate();
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        var res = EPromiseExporter.Build(ProjectSnapshot.Load(db), inv, cfg, o.ContainsKey("force"));
        foreach (var i in res.Issues.Take(30)) Console.WriteLine("  ! " + i);
        if (res.Blocked) return 3;
        var outPath = Req(o, "out");
        if (outPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) EPromiseExporter.WriteCsv(outPath, res, cfg); else EPromiseExporter.WriteXlsx(outPath, res, cfg);
        Console.WriteLine(res.Summary + " -> " + outPath);
        return 0;
    }

    private static int CadImport(string file, Dictionary<string, string> o)
    {
        var db = OpenDb(o);
        var opt = new CadImportOptions
        {
            Building = o.GetValueOrDefault("building", ""), Mode = o.GetValueOrDefault("mode", CadImportModes.Replace).ToUpperInvariant(),
        };
        if (o.TryGetValue("stages", out var st)) opt.Stages = st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var p = CadExchangeImporter.Preview(file, ProjectSnapshot.Load(db), opt);
        Console.WriteLine(p.Summary);
        foreach (var e in p.Errors) Console.WriteLine("  ERROR " + e);
        foreach (var w in p.Warnings.Take(40)) Console.WriteLine("  warn  " + w);
        foreach (var q in p.QtyChanges.Where(q => q.Action != "SAME").Take(60)) Console.WriteLine($"  {q.Action,-6} {q.Room,-10} {q.Stage,-9} {q.Item,-16} {q.OldQty?.ToString("0.##") ?? "-",8} -> {q.NewQty:0.##}  {q.Detail}");
        if (!p.CanCommit) return 3;
        if (o.ContainsKey("commit")) Console.WriteLine(CadExchangeImporter.Commit(p, db));
        else Console.WriteLine("Preview only - add --commit to write.");
        return 0;
    }

    private static int AconexApiTest(Dictionary<string, string> o)
    {
        var cfg = o.TryGetValue("config", out var c) ? AconexApiConfig.Load(c, writeIfMissing: false) : AconexApiConfig.Load();
        foreach (var p in cfg.Validate()) Console.WriteLine("  ! " + p);
        var client = new AconexApiClient(cfg);
        try
        {
            client.Log += Console.WriteLine;
            var projects = client.ProjectsAsync().GetAwaiter().GetResult();
            foreach (var p in projects) Console.WriteLine($"  {p.ProjectId}  {p.Name}");
            client.EnsureLoggedInAsync().GetAwaiter().GetResult();
            return 0;
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
