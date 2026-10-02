using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Documents;
using Raffaello.Core.Remote;

namespace Raffaello.Server.Tests;

/// <summary>Smart document reader on the server: read documents, PostgreSQL full-text search, contract rules and audited bypasses.</summary>
public sealed class DocumentsModuleTests
{
    [PgFact]
    public async Task Read_documents_are_searchable_on_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("qs-a");
        var docs = new RemoteDocumentStore(a);
        var t = new DocText { FileName = "dn.pdf" };
        t.Pages.Add(new DocPage { Number = 1, Text = "Delivery Note 81064344 RAF-P.O-E-045-2026 batch 0013289917", Kind = "DN" });
        t.Pages.Add(new DocPage { Number = 2, Text = "شركة روتس لاندسكيب Mobco-Raffles-SUB-ELE-O28-2O26", Kind = "SUBCONTRACT" });
        DocArchive.Save(docs, t, "/nonexistent/dn.pdf", "DN", "MatDn", "81064344");
        var hit = Assert.Single(docs.Search("81064344"));
        Assert.Equal("MatDn", hit.LinkedTable);
        Assert.Equal(1, hit.Page);
        Assert.Single(docs.Search("1064344"));          // part of a number
        Assert.Equal(2, Assert.Single(docs.Search("لاندسكيب")).Page);
        Assert.Single(docs.Search("SUB-ELE-028"));      // code repaired before indexing
        Assert.Empty(docs.Search("nothing-here-xyz"));
        // re-saving the same file does not duplicate it
        DocArchive.Save(docs, t, "/nonexistent/dn.pdf", "DN", "MatDn", "81064344");
        Assert.Single(docs.All<DocRecord>());
    }

    [PgFact]
    public async Task Rule_bypass_needs_a_reason_and_is_append_only_on_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("qs-a");
        var docs = new RemoteDocumentStore(a);
        var rule = docs.Insert(new ContractRule { ContractNo = "C-1", RuleType = RuleTypes.PaymentStage, ParamsJson = "{\"group\":\"MAIN\",\"stage\":\"1ST FIX\",\"pct\":0.9}" });
        var w = new RuleWarning("C-1", rule.Id, rule.RuleType, "STAGE_PCT", "item 1 at 100 %", "5", "", 2, RuleContexts.Invoice, "ROOTS INV-02 Rev 0|1");
        var ex = Assert.Throws<RemoteRejectedException>(() => docs.Insert(new RuleBypass { ContractNo = "C-1", RuleId = rule.Id, Reason = "" }));
        Assert.Equal(ErrorCodes.BadRequest, ex.Error.Code);
        var b = docs.RecordBypass(w, "agreed with PM", DateTime.Now);
        Assert.True(docs.RuleEngine().RulesOf("C-1").Any());
        b.Reason = "changed later";
        var ex2 = Assert.Throws<RemoteRejectedException>(() => docs.Update(b));
        Assert.Equal(ErrorCodes.AppendOnly, ex2.Error.Code);
    }
}
