using System.Text;
using Raffaello.Core.Data;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Trust;

/// <summary>[trust] Server routes of the trust module.</summary>
public static class TrustApi
{
    public const string AuditVerify = "/api/v1/trust/audit/verify";
    public const string AuditHead = "/api/v1/trust/audit/head";
    public const string AuditChain = "/api/v1/trust/audit/chain";
    public const string AuditSeal = "/api/v1/trust/audit/seal";
    public const string AuditAnchor = "/api/v1/trust/audit/anchor";
    public const string SignaturesVerify = "/api/v1/trust/signatures/verify";
}

/// <summary>
/// "Verify integrity" for whatever the current data source is: the data file (local chain) or the server (the server's own
/// verification, or an independent one where this PC downloads the sealed rows and recomputes every hash itself). The head of
/// every successful check is remembered on this PC (<see cref="AuditAnchorStore"/>), so a later rewrite of history shows.
/// </summary>
public sealed class IntegrityChecker
{
    private readonly AuditAnchorStore _anchors;
    public IntegrityChecker(AuditAnchorStore? anchors = null) => _anchors = anchors ?? new AuditAnchorStore();

    public AuditVerifyReport Verify(IProjectStore store, bool independent = false, IProgress<long>? progress = null)
    {
        AuditVerifyReport rep;
        switch (store)
        {
            case Db db:
                rep = SqliteAuditChain.Verify(db.Path, _anchors.For(db.Path));
                break;
            case RemoteProjectStore r:
                rep = independent ? VerifyIndependently(r.Api, _anchors.For(r.Location), progress) : VerifyOnServer(r.Api, _anchors.For(r.Location));
                break;
            default:
                throw new NotSupportedException($"Integrity check is not available for {store.GetType().Name}.");
        }
        _anchors.Remember(store.Location, rep);
        return rep;
    }

    public static AuditVerifyReport VerifyOnServer(RemoteApi api, IEnumerable<AuditAnchor> anchors)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, TrustApi.AuditVerify.TrimStart('/'))
        {
            Content = new StringContent(RemoteJson.Serialize(new { Anchors = anchors.ToList() }), Encoding.UTF8, "application/json"),
        };
        using var resp = api.Send(req);
        return RemoteJson.Deserialize<AuditVerifyReport>(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) ?? throw new InvalidDataException("Empty verification answer.");
    }

    /// <summary>Downloads the sealed chain page by page and recomputes every hash here - does not trust the server's verdict.</summary>
    public static AuditVerifyReport VerifyIndependently(RemoteApi api, IEnumerable<AuditAnchor> anchors, IProgress<long>? progress = null, int pageSize = 5000)
    {
        AuditChainHead head;
        using (var resp = api.Send(new HttpRequestMessage(HttpMethod.Get, TrustApi.AuditHead.TrimStart('/'))))
            head = RemoteJson.Deserialize<AuditChainHead>(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) ?? new AuditChainHead();
        IEnumerable<AuditChainRow> Pages()
        {
            long after = head.BaseSeq;
            long seen = 0;
            while (true)
            {
                List<AuditChainRow> page;
                using (var resp = api.Send(new HttpRequestMessage(HttpMethod.Get, $"{TrustApi.AuditChain.TrimStart('/')}?afterSeq={after}&take={pageSize}")))
                    page = RemoteJson.Deserialize<List<AuditChainRow>>(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) ?? new();
                foreach (var r in page)
                {
                    if (r.ChainSeq > head.LastSeq) yield break;   // sealed after the head was read: next check covers it
                    yield return r;
                }
                seen += page.Count;
                progress?.Report(seen);
                if (page.Count < pageSize) yield break;
                after = page[^1].ChainSeq ?? after;
            }
        }
        return AuditChainVerifier.Verify(Pages(), head, 0, null, anchors, $"{api.BaseUri} (verified on this PC)");
    }
}
