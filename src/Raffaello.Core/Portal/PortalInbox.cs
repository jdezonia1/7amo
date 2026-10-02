using System.Text;
using Raffaello.Core.Data;
using Raffaello.Core.Remote;
using Raffaello.Core.Statements;

namespace Raffaello.Core.Portal;

/// <summary>
/// The QS's inbox of portal submissions (server mode): list, open the files, preview the statement with the existing site
/// statement importer (duplicates / over-remaining caught as usual), post it to the ledger, reject with a reason, message the
/// subcontractor. Every step is an audited write on the server; the subcontractor sees the status and reasons on the portal.
/// </summary>
public sealed class PortalInbox
{
    private readonly RemoteProjectStore _r;
    static PortalInbox() => PortalEntities.RegisterAll();
    public PortalInbox(RemoteProjectStore r) => _r = r;

    public List<PortalSubmission> Submissions(bool openOnly = false) =>
        _r.All<PortalSubmission>().Where(s => !openOnly || s.Status is PortalSubmissionStatus.Submitted or PortalSubmissionStatus.UnderReview)
            .OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id).ToList();

    public List<PortalMessage> Messages(string? company = null) =>
        _r.All<PortalMessage>().Where(m => company is null || m.Company.Equals(company, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToList();

    public List<PortalCompanySetting> Companies() => _r.All<PortalCompanySetting>().OrderBy(c => c.Name).ToList();

    public PortalCompanySetting SaveCompany(PortalCompanySetting c)
    {
        c.Name = c.Name.Trim().ToUpperInvariant();
        return c.Id > 0 ? _r.Update(c, $"Portal company {c.Name} updated") : _r.Insert(c, $"Portal company {c.Name} added");
    }

    public List<DocumentInfo> Files(PortalSubmission s) => _r.Api.Documents(PortalFileKinds.LinkedTable, s.Id);

    /// <summary>Downloads every file of a submission into <paramref name="folder"/> (checksums verified). Returns the local paths.</summary>
    public List<string> Download(PortalSubmission s, string folder)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var d in Files(s))
        {
            var path = Path.Combine(folder, $"{s.Id}_{d.Id}_{d.FileName}");
            if (!File.Exists(path)) _r.DownloadDocument(d.Id, path);
            paths.Add(path);
        }
        return paths;
    }

    public PortalSubmission MarkUnderReview(PortalSubmission s)
    {
        if (s.Status != PortalSubmissionStatus.Submitted) return s;
        s.Status = PortalSubmissionStatus.UnderReview;
        s.ReviewedBy = _r.User;
        s.ReviewedAt = _r.Clock();
        return _r.Update(s, $"Portal submission #{s.Id} ({s.Company} {s.StatementNo}) under review");
    }

    /// <summary>Downloads the statement workbook and reads it with the standard importer. The statement must be the submitting company's.</summary>
    public (StatementImportResult Result, string Path) PreviewStatement(PortalSubmission s, ProjectSnapshot snapshot, int invoiceNo, string building, string folder)
    {
        if (s.StatementDocumentId <= 0) throw new InvalidOperationException($"Submission #{s.Id} has no statement workbook (drawings / photos only).");
        Directory.CreateDirectory(folder);
        var info = Files(s).FirstOrDefault(d => d.Id == s.StatementDocumentId) ?? throw new InvalidOperationException($"The statement file of submission #{s.Id} is missing on the server.");
        var path = Path.Combine(folder, $"{s.Id}_{info.Id}_{info.FileName}");
        _r.DownloadDocument(info.Id, path);
        var res = SiteStatementService.Read(path, snapshot, invoiceNo, building);
        if (!res.Subcontractor.Equals(s.Company, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The statement says {res.Subcontractor} but it was submitted by {s.Company} - not imported.");
        return (res, path);
    }

    /// <summary>Posts the statement to the ledger (existing importer) and closes the submission as IMPORTED, telling the subcontractor.</summary>
    public int Import(PortalSubmission s, StatementImportResult res, string path, int invoiceNo, string? overReason = null)
    {
        if (s.Status is PortalSubmissionStatus.Imported or PortalSubmissionStatus.Rejected) throw new InvalidOperationException($"Submission #{s.Id} is already {s.Status}.");
        var n = SiteStatementService.Commit(res, _r, path, overReason);
        var fresh = _r.Get<PortalSubmission>(s.Id) ?? s;
        fresh.Status = PortalSubmissionStatus.Imported;
        fresh.ImportedLines = n;
        fresh.InvoiceNo = invoiceNo;
        fresh.ReviewedBy = _r.User;
        fresh.ReviewedAt = _r.Clock();
        var skipped = res.Checks.Count - n;
        fresh.Reason = skipped > 0 ? $"{n} lines posted; {skipped} line(s) above the remaining quantity were not posted." : $"{n} lines posted to invoice {invoiceNo}.";
        _r.Update(fresh, $"Portal submission #{s.Id} imported: {n} claim lines");
        Send(s.Company, $"Statement {res.StatementNo} received", fresh.Reason, s.Id);
        return n;
    }

    public PortalSubmission Reject(PortalSubmission s, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is needed to reject a submission (the subcontractor sees it).");
        var fresh = _r.Get<PortalSubmission>(s.Id) ?? s;
        fresh.Status = PortalSubmissionStatus.Rejected;
        fresh.Reason = reason.Trim();
        fresh.ReviewedBy = _r.User;
        fresh.ReviewedAt = _r.Clock();
        var saved = _r.Update(fresh, $"Portal submission #{s.Id} rejected: {reason}");
        Send(s.Company, $"Statement {s.StatementNo} rejected", reason.Trim(), s.Id);
        return saved;
    }

    public PortalMessage Send(string company, string subject, string body, long submissionId = 0) =>
        _r.Insert(new PortalMessage
        {
            Company = company.Trim().ToUpperInvariant(), Direction = PortalMessageDirections.ToSubcontractor, From = _r.User,
            Subject = subject.Trim(), Body = body.Trim(), SentAt = _r.Clock(), SubmissionId = submissionId,
        }, $"Portal message to {company}: {subject}");

    public PortalMessage MarkRead(PortalMessage m)
    {
        if (m.ReadAt != null || m.Direction != PortalMessageDirections.FromSubcontractor) return m;
        m.ReadAt = _r.Clock();
        return _r.Update(m);
    }

    /// <summary>Portal accounts (ADMIN creates them; QS can list).</summary>
    public List<PortalAccountDto> Accounts()
    {
        using var resp = _r.Api.Send(new HttpRequestMessage(HttpMethod.Get, PortalRoutes.AdminAccounts.TrimStart('/')));
        return RemoteJson.Deserialize<List<PortalAccountDto>>(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) ?? new();
    }

    public PortalAccountDto SaveAccount(PortalAccountDto a)
    {
        var req = new HttpRequestMessage(a.Id > 0 ? HttpMethod.Put : HttpMethod.Post, (a.Id > 0 ? $"{PortalRoutes.AdminAccounts}/{a.Id}" : PortalRoutes.AdminAccounts).TrimStart('/'))
        {
            Content = new StringContent(RemoteJson.Serialize(a), Encoding.UTF8, "application/json"),
        };
        using var resp = _r.Api.Send(req);
        return RemoteJson.Deserialize<PortalAccountDto>(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
    }
}
