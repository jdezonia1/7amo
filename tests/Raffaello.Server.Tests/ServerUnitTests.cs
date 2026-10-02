using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using Raffaello.Server.Backup;
using Raffaello.Server.Data;
using Raffaello.Server.Documents;

namespace Raffaello.Server.Tests;

/// <summary>Tests that need no database.</summary>
public sealed class ServerUnitTests
{
    [Fact]
    public void Permission_matrix_matches_the_roles()
    {
        Assert.True(PermissionMatrix.Can(Roles.Site, Permissions.UploadStatements));
        Assert.False(PermissionMatrix.Can(Roles.Site, Permissions.EditData));
        Assert.True(PermissionMatrix.Can(Roles.Qs, Permissions.PrepareInvoices));
        Assert.False(PermissionMatrix.Can(Roles.Qs, Permissions.ApproveInvoices));
        Assert.True(PermissionMatrix.Can(Roles.Reviewer, Permissions.ApproveInvoices));
        Assert.False(PermissionMatrix.Can(Roles.Reviewer, Permissions.EditData));
        Assert.True(PermissionMatrix.Can(Roles.Admin, Permissions.ManageUsers));
        Assert.False(PermissionMatrix.Can(null, Permissions.Read));
        Assert.Equal("", Roles.Normalize("boss"));
        Assert.Equal(Roles.Qs, Roles.Normalize(" qs "));
    }

    [Fact]
    public void Field_merge_takes_one_sided_changes_and_flags_both_sided_ones()
    {
        var b = new Room { Id = 1, Code = "A", Zone = "Z0", Level = "L1", RowVersion = 1 };
        var mine = new Room { Id = 1, Code = "A", Zone = "MINE", Level = "L1", RoomType = "MINE", RowVersion = 1 };
        var theirs = new Room { Id = 1, Code = "A", Zone = "Z0", Level = "L2", RoomType = "THEIRS", RowVersion = 2 };
        var plan = FieldMerge.Plan(b, mine, theirs);
        Assert.Equal(MergeSide.Mine, plan.Single(f => f.Name == "Zone").Use);
        Assert.Equal(MergeSide.Theirs, plan.Single(f => f.Name == "Level").Use);
        Assert.True(plan.Single(f => f.Name == "RoomType").IsConflict);
        var merged = (Room)FieldMerge.Apply(mine, theirs, plan);
        Assert.Equal(("MINE", "L2", "THEIRS", 2L), (merged.Zone, merged.Level, merged.RoomType, merged.RowVersion));
    }

    [Fact]
    public void Nightly_backup_time_and_retention()
    {
        Assert.Equal(new DateTime(2026, 10, 3, 2, 0, 0), NightlyBackupService.NextRun(new DateTime(2026, 10, 2, 9, 0, 0), "02:00"));
        Assert.Equal(new DateTime(2026, 10, 2, 23, 30, 0), NightlyBackupService.NextRun(new DateTime(2026, 10, 2, 9, 0, 0), "23:30"));

        var dir = Path.Combine(Path.GetTempPath(), "raffaello-ret-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var f = Path.Combine(dir, $"raffaello_202601{10 + i:00}_020000.dump");
                File.WriteAllText(f, "x");
                File.SetLastWriteTime(f, DateTime.Now.AddDays(-60 + i));
            }
            var runner = new BackupRunner("Host=x;Database=y", new ServerOptions { BackupFolder = dir, BackupRetentionDays = 30, BackupKeepMin = 3 }, dir);
            Assert.Equal(7, runner.ApplyRetention());
            Assert.Equal(3, runner.List().Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Document_names_are_made_safe()
    {
        Assert.Equal("a_b.pdf", DocumentStore.SafeName("a:b.pdf"));
        var traversal = DocumentStore.SafeName("..\\..\\evil.pdf");
        Assert.DoesNotContain("\\", traversal);
        Assert.DoesNotContain("/", DocumentStore.SafeName("../../evil.pdf"));
        Assert.EndsWith("evil.pdf", traversal);
        Assert.Equal("file.bin", DocumentStore.SafeName("  "));
        Assert.Equal("file.bin", DocumentStore.SafeName(".."));
        Assert.Equal("evil.pdf", DocumentStore.SafeName("C:\\temp\\evil.pdf"));
        Assert.True(DocumentStore.SafeName(new string('x', 300) + ".pdf").Length <= 120);
    }

    [Fact]
    public void Every_core_entity_type_is_registered_on_the_server_and_the_client()
    {
        foreach (var t in Core.Data.Db.EntityTypes)
        {
            Assert.Equal(t, EntityRegistry.Find(Core.Data.Db.TableOf(t)));
            Assert.Equal(t, EntityMeta.Find(Core.Data.Db.TableOf(t)));
        }
        Assert.Contains(EntityMeta.ForeignKeys(typeof(DnLine)), p => p.Name == nameof(DnLine.LineId));
        Assert.Contains(EntityMeta.ForeignKeys(typeof(SubInvoiceLine)), p => p.Name == nameof(SubInvoiceLine.SubInvoiceId));
    }

    [Fact]
    public void Remote_settings_default_to_local()
    {
        var s = new RemoteSettings();
        Assert.False(s.IsServer);
        s.Mode = DataSources.Server;
        Assert.False(s.IsServer);   // no URL yet
        s.ServerUrl = "http://raffaello-srv:5180";
        Assert.True(s.IsServer);
        Assert.Contains("raffaello-srv_5180", s.EffectiveCacheFolder());
        Assert.Throws<ArgumentException>(() => new RemoteApi("not a url", null, false, "pc", "c", TimeSpan.FromSeconds(5)));
    }
}
