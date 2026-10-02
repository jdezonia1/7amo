using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Documents;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;
using static Raffaello.Server.Data.PgMap;

namespace Raffaello.Server.Modules;

/// <summary>
/// Read documents (evidence index, page text, extracted fields, learned templates) and contract intelligence (terms, clauses, rules,
/// bypasses), plus full-text search over every read page: PostgreSQL full text ('simple' configuration - no stemming, so numbers,
/// codes and Arabic words match as written) with a substring fallback for partial numbers / codes.
/// </summary>
public sealed class DocumentsServerModule : IServerModule
{
    public string Name => "Documents";
    public IEnumerable<Type> EntityTypes => DocumentEntities.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new BypassReasonGuard() };

    private static int _indexed;

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet(ApiRoutes.DocSearch, async (string? q, int? take, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var hits = await SearchAsync(ds, q ?? "", Math.Clamp(take ?? 50, 1, 500), ct);
            return Results.Text(JsonSerializer.Serialize(hits, RemoteJson.Options), "application/json");
        });
    }

    internal static async Task<List<DocSearchHit>> SearchAsync(NpgsqlDataSource ds, string query, int take, CancellationToken ct)
    {
        var words = ArabicText.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();
        await using var c = await ds.OpenConnectionAsync(ct);
        if (Interlocked.Exchange(ref _indexed, 1) == 0)
        {
            await using var ix = new NpgsqlCommand($"""CREATE INDEX IF NOT EXISTS ix_docpagetexts_fts ON {Q("DocPageTexts")} USING GIN (to_tsvector('simple', "NormText"))""", c);
            try { await ix.ExecuteNonQueryAsync(ct); } catch (PostgresException) { Interlocked.Exchange(ref _indexed, 0); }
        }
        // whole words through the full-text index, every word also as a substring (partial DN numbers, codes inside longer tokens)
        var likes = string.Join(" AND ", words.Select((_, i) => $"p.\"NormText\" LIKE @w{i}"));
        var sql = $"""
            SELECT p."DocRecordId", COALESCE(d."FileName", ''), COALESCE(d."DocType", p."Kind"), COALESCE(d."LinkedTable", ''), COALESCE(d."LinkedKey", ''), p."Page", p."NormText",
                   ts_rank(to_tsvector('simple', p."NormText"), plainto_tsquery('simple', @q)) AS rank
            FROM {Q("DocPageTexts")} p LEFT JOIN {Q("DocRecords")} d ON d."Id" = p."DocRecordId"
            WHERE to_tsvector('simple', p."NormText") @@ plainto_tsquery('simple', @q) OR ({likes})
            ORDER BY rank DESC, p."DocRecordId" DESC, p."Page"
            LIMIT @take
            """;
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("q", string.Join(' ', words));
        cmd.Parameters.AddWithValue("take", take);
        for (var i = 0; i < words.Length; i++) cmd.Parameters.AddWithValue("w" + i, "%" + words[i].Replace("%", "\\%").Replace("_", "\\_") + "%");
        var res = new List<DocSearchHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            res.Add(new DocSearchHit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), DocumentStoreExtensions.Snippet(r.GetString(6), words[0]), r.GetFloat(7)));
        return res;
    }
}

/// <summary>A contract-rule bypass must carry a reason (the same rule as the desktop); bypass rows cannot be edited afterwards.</summary>
public sealed class BypassReasonGuard : IWriteGuard
{
    public void Check(WriteCheck c)
    {
        if (c.Type != typeof(RuleBypass)) return;
        if (c.Kind == WriteKind.Update) throw new WriteRejectedException(409, ErrorCodes.AppendOnly, "A bypass record cannot be changed - record a new one.");
        if (c.Kind == WriteKind.Insert && (((RuleBypass)c.Entity).Reason?.Trim().Length ?? 0) < 3)
            throw new WriteRejectedException(422, ErrorCodes.BadRequest, "A bypass needs a short reason.");
    }
}
