using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Remote;
using static Raffaello.Server.Data.PgMap;

namespace Raffaello.Server.Data;

public enum WriteKind { Insert, Update, Delete }

/// <summary>Who is writing. Trusted = server-side tooling (CLI, migration, seeding): permission checks are skipped, business rules are not.</summary>
public sealed record StoreIdentity(string User, string Role, string Machine, string ClientId = "", string AuthType = "", bool Trusted = false)
{
    public static StoreIdentity System(string user = "SYSTEM") => new(user, Roles.Admin, Environment.MachineName, "", "system", Trusted: true);
    public bool Can(string permission) => Trusted || PermissionMatrix.Can(Role, permission);
}

/// <summary>One write about to happen inside the request transaction. Guards may query through <see cref="Tx"/> and throw.</summary>
public sealed class WriteCheck
{
    public required WriteKind Kind { get; init; }
    public required Type Type { get; init; }
    public required string Table { get; init; }
    public required Entity Entity { get; init; }
    /// <summary>The stored row (update / delete), read FOR UPDATE.</summary>
    public Entity? Before { get; init; }
    public required StoreIdentity Who { get; init; }
    public required PgTx Tx { get; init; }
    /// <summary>True when the row (table, id) was inserted earlier in the same request.</summary>
    public required Func<string, long, bool> InsertedInRequest { get; init; }
    public required Func<DateTime> Clock { get; init; }
}

/// <summary>A server-side business rule or permission check, run inside the write transaction before each row is written.</summary>
public interface IWriteGuard
{
    /// <summary>Before the row is written: validate, throw <see cref="WriteRejectedException"/> to refuse (the request rolls back).</summary>
    void Check(WriteCheck c);

    /// <summary>After the row is written (same transaction; inserted rows have their id, updated rows their new RowVersion).</summary>
    void AfterWrite(WriteCheck c) { }
}

/// <summary>Role x table permission matrix for entity writes.</summary>
public sealed class PermissionGuard : IWriteGuard
{
    private static readonly HashSet<string> StatusFields = new() { "Status", "ApprovedAt", "SubmittedAt", "Locked", "RejectionReason", "AconexWorkflowNo", "CertifiedAt", "UpdatedAt", "UpdatedBy", "RowVersion" };

    public void Check(WriteCheck c)
    {
        if (c.Who.Trusted) return;
        var need = Required(c);
        if (need.Any(c.Who.Can)) return;
        throw new WriteRejectedException(403, ErrorCodes.Forbidden,
            $"{c.Who.User} ({(c.Who.Role.Length == 0 ? "no role" : c.Who.Role)}) may not {c.Kind.ToString().ToLowerInvariant()} {c.Table}. Needs {string.Join(" or ", need)}.");
    }

    private static string[] Required(WriteCheck c)
    {
        var t = c.Type;
        if (t == typeof(InvoiceTemplateRow)) return new[] { Permissions.ManageTemplates };
        if (t == typeof(SiteStatement)) return new[] { Permissions.UploadStatements };
        if (t == typeof(ClaimLine) && c.Kind == WriteKind.Insert && ((ClaimLine)c.Entity).Source == "STATEMENT") return new[] { Permissions.UploadStatements };
        if (t == typeof(SubInvoice) || t == typeof(Invoice))
        {
            // a status-only change (head-office outcome) may also be recorded by a reviewer
            if (c.Kind == WriteKind.Update && c.Before != null && ChangedFields(c.Before, c.Entity).All(StatusFields.Contains))
                return new[] { Permissions.PrepareInvoices, Permissions.ApproveInvoices };
            return new[] { Permissions.PrepareInvoices };
        }
        if (t == typeof(SubInvoiceLine) || t == typeof(InvoiceLine)) return new[] { Permissions.PrepareInvoices };
        // [assistant] begin: every signed-in user keeps his own chat history, reminders and notification rules (owner checked by AssistantOwnerGuard)
        if (Raffaello.Core.Assistant.AssistantEntityTypes.All.Contains(t)) return new[] { Permissions.Read };
        // [assistant] end
        return new[] { Permissions.EditData };
    }

    public static IEnumerable<string> ChangedFields(Entity before, Entity after) =>
        EntityRegistry.Props(before.GetType()).Where(p => !Equals(p.GetValue(before), p.GetValue(after))).Select(p => p.Name);
}

/// <summary>
/// The room ledger on the server: append-only (no deletes, no change of the claimed quantity / key), and REMAINING validated
/// at save time. Each insert takes a transaction-scoped advisory lock on the ledger key (room | stage | item) and re-reads
/// PROJECT QTY and every claim for the key inside the transaction, so two users can never both claim the last units.
/// Imports of history (tracker, splits, reversals) are not re-validated: they record what was already invoiced.
/// </summary>
public sealed class LedgerGuard : IWriteGuard
{
    public static readonly string[] ImmutableFields =
        { nameof(ClaimLine.Qty), nameof(ClaimLine.Room), nameof(ClaimLine.Stage), nameof(ClaimLine.Item), nameof(ClaimLine.Subcontractor), nameof(ClaimLine.InvoiceNo), nameof(ClaimLine.Building), nameof(ClaimLine.Rework) };

    public ISet<string> ValidatedSources { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MANUAL", "STATEMENT", "" };

    public void Check(WriteCheck c)
    {
        if (c.Type != typeof(ClaimLine)) return;
        switch (c.Kind)
        {
            case WriteKind.Delete:
                throw new WriteRejectedException(409, ErrorCodes.AppendOnly, $"The ledger is append-only: claim #{c.Entity.Id} cannot be deleted - post a reversal instead.");
            case WriteKind.Update:
                var changed = PermissionGuard.ChangedFields(c.Before!, c.Entity).Intersect(ImmutableFields).ToList();
                if (changed.Count > 0)
                    throw new WriteRejectedException(409, ErrorCodes.AppendOnly, $"The ledger is append-only: {string.Join(", ", changed)} of claim #{c.Entity.Id} cannot change - post a reversal and a new line.");
                return;
            case WriteKind.Insert:
                ValidateRemaining(c, (ClaimLine)c.Entity);
                return;
        }
    }

    private void ValidateRemaining(WriteCheck c, ClaimLine line)
    {
        if (line.Rework || line.Qty <= LedgerRules.Eps || !ValidatedSources.Contains(line.Source ?? "")) return;
        if (line.IsOver && !string.IsNullOrWhiteSpace(line.OverReason)) return;   // explicitly posted OVER with a reason
        var key = LedgerKeys.Key(line.Room, line.Stage, line.Item);
        c.Tx.Lock("ledger|" + key);
        var parts = key.Split('|');
        const string where = """WHERE upper(btrim("Room")) = @r AND upper(btrim("Stage")) = @s AND upper(btrim("Item")) = @i""";
        var caps = c.Tx.Query<RoomQty>($"SELECT * FROM {Q("RoomQtys")} {where}", ("r", parts[0]), ("s", parts[1]), ("i", parts[2]));
        var claims = c.Tx.Query<ClaimLine>($"SELECT * FROM {Q("ClaimLines")} {where}", ("r", parts[0]), ("s", parts[1]), ("i", parts[2]));
        var balance = LedgerRules.Balance(caps, claims, line.Room, line.Stage, line.Item);
        var check = LedgerRules.Check(balance, line.Qty, null);
        if (!check.CanPost)
            throw new WriteRejectedException(422, ErrorCodes.RemainingExceeded, check.Message);
    }
}

/// <summary>
/// Subcontractor invoice approvals. Internal chain DRAFT -> CHECKED -> APPROVED -> SUBMITTED: CHECKED / APPROVED are stamps
/// (who / when / role / RowVersion) given through the approvals endpoint; changing Status to SUBMITTED needs a valid APPROVED
/// stamp on the current RowVersion when <see cref="RequireInternalApproval"/> is on. Every status change is stamped. Locked
/// (head-office approved) invoices and their lines cannot change.
/// </summary>
public sealed class InvoiceGuard : IWriteGuard
{
    public bool RequireInternalApproval { get; init; } = true;

    public void Check(WriteCheck c)
    {
        if (c.Type == typeof(SubInvoiceLine)) CheckLine(c);
        else if (c.Type == typeof(SubInvoice)) CheckHeader(c);
    }

    public void AfterWrite(WriteCheck c)
    {
        if (c.Type == typeof(SubInvoice))
        {
            var a = (SubInvoice)c.Entity;
            if (c.Kind == WriteKind.Insert)
            {
                if (a.Status != SubInvoiceStatus.Draft) Stamp(c, ApprovalStages.Status, "", a.Status, "created with this status (imported)");
                return;
            }
            if (c.Kind != WriteKind.Update) return;
            var b = (SubInvoice)c.Before!;
            if (b.Status == a.Status) return;
            if (a.Status == SubInvoiceStatus.Submitted) Stamp(c, ApprovalStages.Submitted, b.Status, a.Status, a.AconexWorkflowNo.Length > 0 ? "Aconex " + a.AconexWorkflowNo : "");
            else Stamp(c, ApprovalStages.Status, b.Status, a.Status, a.Status == SubInvoiceStatus.Rejected ? a.RejectionReason : "");
        }
        else if (c.Type == typeof(Invoice) && c.Kind == WriteKind.Update)
        {
            var b = (Invoice)c.Before!; var a = (Invoice)c.Entity;
            if (b.Status != a.Status) Stamp(c, ApprovalStages.Status, b.Status, a.Status, a.Notes);
        }
    }

    private static void CheckLine(WriteCheck c)
    {
        if (c.Who.Trusted) return;
        var line = (SubInvoiceLine)c.Entity;
        var parentId = c.Kind == WriteKind.Insert ? line.SubInvoiceId : ((SubInvoiceLine)(c.Before ?? c.Entity)).SubInvoiceId;
        if (c.InsertedInRequest(EntityRegistry.TableOf(typeof(SubInvoice)), parentId)) return;
        var locked = c.Tx.Scalar("""SELECT "Locked" FROM "SubInvoices" WHERE "Id" = @id""", ("id", parentId));
        if (locked is true)
            throw new WriteRejectedException(409, ErrorCodes.Locked, $"Invoice #{parentId} is approved and locked - its lines cannot change.");
    }

    private void CheckHeader(WriteCheck c)
    {
        if (c.Kind == WriteKind.Insert) return;
        var a = (SubInvoice)c.Entity;
        var b = (SubInvoice)c.Before!;
        if (b.Locked && !c.Who.Trusted)
            throw new WriteRejectedException(409, ErrorCodes.Locked, $"{b.Title} is approved and locked.");
        if (c.Kind == WriteKind.Delete || b.Status == a.Status) return;

        if (a.Status == SubInvoiceStatus.Submitted)
        {
            if (!c.Who.Can(Permissions.PrepareInvoices))
                throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{c.Who.User} ({c.Who.Role}) may not submit invoices.");
            if (RequireInternalApproval)
            {
                var stamps = c.Tx.Query<ApprovalStampDto>("""SELECT * FROM "ApprovalStamps" WHERE "TableName" = 'SubInvoices' AND "RowId" = @id ORDER BY "Id" """, ("id", b.Id));
                var approved = stamps.LastOrDefault(s => s.Stage == ApprovalStages.Approved);
                if (approved is null || approved.RowVersion != b.RowVersion)
                    throw new WriteRejectedException(422, ErrorCodes.ApprovalRequired,
                        approved is null
                            ? $"{b.Title} needs CHECKED and APPROVED stamps before it is submitted."
                            : $"{b.Title} was changed after it was approved ({approved.By}, {approved.At:dd-MMM HH:mm}) - check and approve it again.");
            }
            return;
        }
        if (a.Status is SubInvoiceStatus.Approved or SubInvoiceStatus.Rejected && !(c.Who.Can(Permissions.PrepareInvoices) || c.Who.Can(Permissions.ApproveInvoices)))
            throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{c.Who.User} ({c.Who.Role}) may not record the head-office outcome.");
    }

    private static void Stamp(WriteCheck c, string stage, string from, string to, string note) =>
        InsertStamp(c.Tx, c.Table, c.Entity.Id, stage, from, to, c.Entity.RowVersion, c.Who, c.Clock(), note);

    public static long InsertStamp(PgTx tx, string table, long rowId, string stage, string from, string to, long rowVersion, StoreIdentity who, DateTime at, string note) =>
        Convert.ToInt64(tx.Scalar("""
            INSERT INTO "ApprovalStamps" ("TableName", "RowId", "Stage", "FromStatus", "ToStatus", "RowVersion", "By", "Role", "At", "Note")
            VALUES (@t, @r, @s, @f, @to, @v, @by, @role, @at, @n) RETURNING "Id"
            """, ("t", table), ("r", rowId), ("s", stage), ("f", from), ("to", to), ("v", rowVersion), ("by", who.User), ("role", who.Role), ("at", at), ("n", note ?? "")), System.Globalization.CultureInfo.InvariantCulture);
}
