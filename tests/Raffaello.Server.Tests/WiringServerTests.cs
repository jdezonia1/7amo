using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Assistant;
using Raffaello.Core.Cables;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;
using Raffaello.Core.Insights;
using Raffaello.Core.Portal;
using Raffaello.Core.Remote;
using Raffaello.Core.Statements;
using Raffaello.Core.Trust;
using Raffaello.Core.Wiring;

namespace Raffaello.Server.Tests;

/// <summary>Cross-module wiring with the server as data source: reset of every module table, archive search, obligations, preview bypass.</summary>
public sealed class WiringServerTests
{
    private static SigningKeyStore Keys() =>
        new(Path.Combine(Path.GetTempPath(), "raffaello-test-keys", Guid.NewGuid().ToString("N")), new PassphraseKeyProtector("pw") { Iterations = 1000 });

    [PgFact]
    public async Task Server_reset_clears_every_module_table_and_keeps_keys_signatures_portal_settings_and_accounts()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        using var admin = srv.Client("admin", Roles.Admin);
        var room = qs.Insert(new Room { Building = Buildings.Branded, Code = "P2-106" });
        new RemoteInsightsStore(qs).Save(new InsightNorm { Material = "CONDUIT 20MM", Unit = "M", PerPoint = 5 });
        new RemoteAssemblyStore(qs).SavePrice(new AsmPrice { Source = PriceSources.Manual, Description = "PVC conduit 20 mm", Unit = "M", Price = 2 });
        new DrawingStoreSelector(() => qs).SaveSymbol(new DwgSymbol { Name = "DOWNLIGHT", System = "LIGHT" });
        var cables = new CableService(CableStore.For(qs));
        cables.Import(new[] { new CableClaim { Building = Buildings.Hotel, Subcontractor = "SUBA", InvoiceNo = 1, RawStage = "PULLING", FromRaw = "SMDB-HT-Z1-LB2-CM-01", ToRaw = "LDB-HT-Z1-LB2-03", SizeRaw = "4x16", Qty = 100, SitePct = 1, WirPct = 1, Source = "TEST", SourceKey = "T|1" } }, null, "test");
        var docs = new RemoteDocumentStore(qs);
        docs.SaveRead(new DocRecord { FileName = "c.pdf", Sha256 = "x" }, new[] { new DocPageText { Page = 1, Text = "retention clause" } }, Array.Empty<DocField>());
        new RemoteAssistantStore(qs).Insert(new AssistantReminder { Owner = qs.User, Text = "check", Due = DateTime.Today });
        var inbox = new PortalInbox(qs);
        inbox.SaveCompany(new PortalCompanySetting { Name = "ALPHA", DisplayName = "Alpha Electrical", Building = Buildings.Branded });
        inbox.Send("ALPHA", "Welcome", "Please use the portal.");
        var trust = new RemoteTrustStore(qs);
        trust.RegisterKey(Keys().GetOrCreate(qs.User), new SignerInfo(qs.Me!.UserName, qs.Me.DisplayName, qs.Me.Role));
        using (var http = srv.Http("admin", Roles.Admin))
        {
            var r = await http.PostAsJsonAsync(PortalRoutes.AdminAccounts.TrimStart('/'), new PortalAccountDto { UserName = "alpha.site", DisplayName = "alpha", Company = "ALPHA", Password = "Portal-pass-123" }, RemoteJson.Options);
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        }

        admin.ClearAll();
        qs.Cache.Invalidate();

        Assert.Empty(qs.All<Room>());
        Assert.Empty(qs.All<InsightNorm>());
        Assert.Empty(qs.All<AsmPrice>());
        Assert.Empty(qs.All<DwgSymbol>());
        Assert.Empty(qs.All<CableClaim>());
        Assert.Empty(qs.All<CableRun>());
        Assert.Empty(qs.All<DocRecord>());
        Assert.Empty(docs.Search("retention"));
        Assert.Empty(qs.All<AssistantReminder>());
        Assert.Empty(qs.All<PortalMessage>());
        // kept: signing keys (and signatures), portal company settings, portal accounts
        Assert.Single(trust.Keys());
        Assert.Single(qs.All<PortalCompanySetting>());
        using (var http = srv.Http("admin", Roles.Admin))
        {
            var list = await http.GetStringAsync(PortalRoutes.AdminAccounts.TrimStart('/'));
            Assert.Contains("alpha.site", list);
        }
        // ids continue after the reset: a new record never reuses the id of a cleared (possibly signed) one
        var again = qs.Insert(new Room { Building = Buildings.Branded, Code = "P2-107" });
        Assert.True(again.Id > room.Id);
    }

    [PgFact]
    public async Task Assistant_search_reads_the_server_full_text_archive_with_citations()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        var docs = new RemoteDocumentStore(qs);
        var t = new DocText { FileName = "SUB-ELE-001 signed.pdf" };
        t.Pages.Add(new DocPage { Number = 1, Text = "Subcontract agreement", Kind = "SUBCONTRACT" });
        t.Pages.Add(new DocPage { Number = 4, Text = "Delay penalty SAR 5000 per week, capped at 10 percent", Kind = "SUBCONTRACT" });
        DocArchive.Save(docs, t, "/nonexistent/sub.pdf", "SUBCONTRACT", "Contract", "SUB-ELE-001");
        var search = new ArchiveDocumentSearch(docs);
        var hit = Assert.Single(search.Search("penalty", 10));
        Assert.Equal(4, hit.Page);
        Assert.Equal("Contract", hit.LinkedTable);
        Assert.Equal("[[contract:SUB-ELE-001]]", ArchiveDocumentSearch.LinkedCitation(hit.LinkedTable, hit.LinkedKey)!.Token);
        Assert.Contains("document archive", search.Name);
    }

    [PgFact]
    public async Task Server_brief_and_needs_today_include_the_contract_obligations()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        new RemoteDocumentStore(qs).Insert(new ContractTerms { ContractNo = "SUB-ELE-001", Subcontractor = "ROOTS", CompletionDate = DateTime.Today.AddDays(6) });
        using var http = srv.Http("qs");
        var text = await http.GetStringAsync("api/v1/assistant/brief");
        Assert.Contains("HANDOVER: SUB-ELE-001 ROOTS", text);
        var store = srv.App.Services.GetRequiredService<Raffaello.Server.Api.StoreFactory>().For(Raffaello.Server.Data.StoreIdentity.System("TEST"));
        var (_, queue) = Raffaello.Core.Notify.StoreBriefData.Load(store, DateTime.Today);
        Assert.Contains(queue, q => q.Category == "CONTRACT" && q.Target.Key == "SUB-ELE-001");
    }

    [PgFact]
    public async Task Statement_preview_bypass_is_recorded_on_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        qs.Insert(new Contract { ContractNo = "SUB-T-1", Subcontractor = "ROOTS", Building = Buildings.Hotel });
        var docs = new RemoteDocumentStore(qs);
        docs.Insert(new ContractRule { ContractNo = "SUB-T-1", RuleType = RuleTypes.HeightBand, ParamsJson = "{\"meters\":4.5}", Summary = "Rates split at 4.5 m" });
        var cables = new CableService(CableStore.For(qs));
        cables.Import(new[] { new CableClaim { Building = Buildings.Hotel, Subcontractor = "SAFIA", InvoiceNo = 1, RawStage = "PULLING", FromRaw = "SMDB-HT-Z1-LB2-CM-01", ToRaw = "LDB-HT-Z1-LB2-03", SizeRaw = "4x16", Qty = 100, SitePct = 1, WirPct = 1, Source = "TEST", SourceKey = "T|1" } }, null, "INV 1");
        var snap = new Raffaello.Core.Data.ProjectSnapshot { Contracts = qs.All<Contract>() };
        var res = new StatementImportResult { Subcontractor = "ROOTS", StatementNo = "ST-ROOTS-02" };
        res.Claims.Add(new ClaimLine { Building = Buildings.Hotel, Subcontractor = "ROOTS", InvoiceNo = 2, Room = "H-101", Stage = Stages.First, Item = "LIGHT", Qty = 10, QtyAbove45 = 4, SitePct = 1, WirPct = 1 });
        res.CableClaims.Add(new CableClaim { Building = Buildings.Hotel, Subcontractor = "ROOTS", InvoiceNo = 2, RawStage = "PULLING", FromRaw = "LDB-HT-Z1-LB2-03", ToRaw = "SMDB-HT-Z1-LB2-CM-01", SizeRaw = "4x16", Qty = 90, SitePct = 1, WirPct = 1, Source = "STATEMENT", SourceKey = "ST|1" });
        CableHooks.AnnotateStatement(res, qs);
        StatementPreviewChecks.RuleWarnings(res, snap, docs);
        Assert.Equal(1, StatementPreviewChecks.BypassCableFlags(qs, res.CableFlags.Where(f => f.Code == CableFlagCodes.Duplicate), "re-pulled, agreed with the engineer"));
        Assert.Equal(1, StatementPreviewChecks.BypassRuleWarnings(docs, res.RuleWarnings, "checked on site", DateTime.Now));
        StatementPreviewChecks.Refresh(res, qs, snap, docs);
        Assert.True(res.CableFlags.Single(f => f.Code == CableFlagCodes.Duplicate).IsBypassed);
        Assert.All(res.RuleWarnings, w => Assert.True(w.IsBypassed));
        Assert.Equal("qs", qs.All<CableFlagDecision>().Single().By);
        Assert.Equal("qs", docs.All<RuleBypass>().Single().BypassedBy);
    }
}
