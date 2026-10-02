using Microsoft.Data.Sqlite;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Trust;

namespace Raffaello.Core.Tests;

/// <summary>[trust] Hash-chained audit log on the desktop data file.</summary>
public sealed class TrustAuditChainTests
{
    private static Db Seeded(int rooms = 5)
    {
        var db = TestData.NewDb();
        for (var i = 0; i < rooms; i++) db.Insert(new Room { Building = Buildings.Branded, Code = $"P2-{100 + i}", Level = "L2" });
        var r = db.All<Room>()[0];
        r.AreaType = "APARTMENT";
        db.Update(r, "area type");
        db.LogEvent("TEST", "an event");
        return db;
    }

    private static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Every_write_is_sealed_and_the_chain_verifies()
    {
        var db = Seeded();
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.True(rep.Ok, rep.ToText());
        Assert.Equal(7, rep.RowsChecked);
        Assert.Equal(0, rep.Unsealed);
        Assert.Equal(1, rep.FirstSeq);
        Assert.Equal(7, rep.LastSeq);
        Assert.Contains("INTACT", rep.Summary);
        // audit entries still read normally (extra chain columns are ignored)
        Assert.Equal(7, db.RecentAudit(100).Count);
    }

    [Fact]
    public void Edited_row_is_detected()
    {
        var db = Seeded();
        Exec(db.Path, "UPDATE AuditLog SET Summary = 'nothing happened' WHERE ChainSeq = 3");
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.False(rep.Ok);
        var p = Assert.Single(rep.Problems);
        Assert.Equal(AuditProblemKinds.Edited, p.Kind);
        Assert.Equal(3, p.Seq);
    }

    [Fact]
    public void Edited_and_resealed_row_breaks_the_link_to_the_next_row()
    {
        var db = Seeded();
        // a careful forger recomputes the hash of the row he changed - the next row still points at the old hash
        var rows = Rows(db.Path);
        var target = rows.Single(r => r.ChainSeq == 2);
        target.User = "someone-else";
        var forged = AuditHash.Compute(target.PrevHash!, target, 2);
        Exec(db.Path, $"UPDATE AuditLog SET User = 'someone-else', Hash = '{forged}' WHERE ChainSeq = 2");
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.False(rep.Ok);
        Assert.Contains(rep.Problems, p => p.Kind == AuditProblemKinds.LinkBroken && p.Seq == 3);
    }

    [Fact]
    public void Deleted_rows_and_deleted_tail_are_detected()
    {
        var db = Seeded();
        Exec(db.Path, "DELETE FROM AuditLog WHERE ChainSeq IN (3, 4)");
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.Contains(rep.Problems, p => p.Kind == AuditProblemKinds.Deleted && p.Seq == 3 && p.Message.Contains("#3..#4"));

        var db2 = Seeded();
        Exec(db2.Path, "DELETE FROM AuditLog WHERE ChainSeq = 7");
        var rep2 = SqliteAuditChain.Verify(db2.Path);
        Assert.Contains(rep2.Problems, p => p.Kind == AuditProblemKinds.TailDeleted);
    }

    [Fact]
    public void Rewriting_the_whole_chain_is_caught_by_an_anchor()
    {
        var db = Seeded();
        var anchors = new AuditAnchorStore(Path.Combine(TestData.TempDir(), "anchors.json"));
        var first = SqliteAuditChain.Verify(db.Path, anchors.For(db.Path));
        Assert.True(first.Ok);
        anchors.Remember(db.Path, first);

        // forger rewrites row 2 and re-seals every row after it, and the head
        var rows = Rows(db.Path).OrderBy(r => r.ChainSeq).ToList();
        rows[1].Summary = "rewritten";
        var prev = AuditHash.Genesis;
        using (var c = new SqliteConnection($"Data Source={db.Path}"))
        {
            c.Open();
            foreach (var r in rows)
            {
                var h = AuditHash.Compute(prev, r, r.ChainSeq!.Value);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE AuditLog SET Summary=@s, PrevHash=@p, Hash=@h WHERE Id=@id";
                cmd.Parameters.AddWithValue("@s", r.Summary ?? "");
                cmd.Parameters.AddWithValue("@p", prev);
                cmd.Parameters.AddWithValue("@h", h);
                cmd.Parameters.AddWithValue("@id", r.Id);
                cmd.ExecuteNonQuery();
                prev = h;
            }
            using var head = c.CreateCommand();
            head.CommandText = "UPDATE AuditChainHead SET LastHash=@h";
            head.Parameters.AddWithValue("@h", prev);
            head.ExecuteNonQuery();
        }
        Assert.True(SqliteAuditChain.Verify(db.Path).Ok);   // internally consistent...
        var rep = SqliteAuditChain.Verify(db.Path, anchors.For(db.Path));
        Assert.False(rep.Ok);                                  // ...but not what this PC saw before
        Assert.Contains(rep.Problems, p => p.Kind == AuditProblemKinds.AnchorMismatch);
    }

    [Fact]
    public void Data_reset_starts_a_new_segment_that_still_verifies()
    {
        var db = Seeded();
        db.ClearAll();
        db.Insert(new Room { Building = Buildings.Hotel, Code = "H-1" });
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.True(rep.Ok, rep.ToText());
        Assert.Equal(8, rep.FirstSeq);
        Assert.NotNull(rep.ResetAt);
        Assert.Contains("reset", rep.Summary);
    }

    [Fact]
    public void Rows_written_by_an_older_version_are_sealed_by_the_next_write()
    {
        var db = Seeded();
        Exec(db.Path, "INSERT INTO AuditLog (At, User, Machine, TableName, RowId, Action, Summary, Changes) VALUES ('2026-10-01T08:00:00.000','old-app','PC9','',0,'EVENT','old version','')");
        var before = SqliteAuditChain.Verify(db.Path);
        Assert.Equal(1, before.Unsealed);
        db.LogEvent("TEST", "new version write");
        var after = SqliteAuditChain.Verify(db.Path);
        Assert.True(after.Ok);
        Assert.Equal(0, after.Unsealed);
        Assert.Equal(9, after.LastSeq);
    }

    [Fact]
    public void Side_store_writes_are_sealed_too()
    {
        var db = Seeded();
        var trust = new SqliteTrustStore(db);
        var keys = new SigningKeyStore(TestData.TempDir(), new PassphraseKeyProtector("pw") { Iterations = 1000 });
        trust.RegisterKey(keys.GetOrCreate("tester"), new SignerInfo("tester", "Tester", "QS"));
        var rep = SqliteAuditChain.Verify(db.Path);
        Assert.True(rep.Ok, rep.ToText());
        Assert.Equal(8, rep.LastSeq);
    }

    [Fact]
    public void Canonical_content_is_unambiguous()
    {
        var a = new AuditChainRow { Id = 1, At = new DateTime(2026, 10, 2, 9, 0, 0), User = "a|b", Summary = "c" };
        var b = new AuditChainRow { Id = 1, At = new DateTime(2026, 10, 2, 9, 0, 0), User = "a", Summary = "b|c" };
        Assert.NotEqual(AuditHash.Content(a, 1), AuditHash.Content(b, 1));
        var n = new AuditChainRow { Id = 1, At = a.At, Changes = null };
        var e = new AuditChainRow { Id = 1, At = a.At, Changes = "" };
        Assert.NotEqual(AuditHash.Compute("x", n, 1), AuditHash.Compute("x", e, 1));
    }

    private static List<AuditChainRow> Rows(string path)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        return Db.Query<AuditChainRow>(c, null, "SELECT * FROM AuditLog");
    }
}
