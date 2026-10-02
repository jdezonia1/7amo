using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Assistant;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Tests;

/// <summary>Synthetic project for the assistant tests (no company data): two rooms, a ledger, a rejected invoice, a DN.</summary>
internal sealed class AssistantEnv : IDisposable
{
    public required ProjectService Project { get; init; }
    public required SqliteAssistantStore Store { get; init; }
    public required SqliteMaterialsStore Materials { get; init; }
    public required AssistantData Data { get; init; }
    public string Dir { get; init; } = "";
    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    public static AssistantEnv Create(AssistantSettings? settings = null)
    {
        var dir = TestData.TempDir();
        var app = new AppSettings { DataFilePath = Path.Combine(dir, "assistant.db"), SeedDemoData = false, UserName = "tester", DocumentsRoot = Path.Combine(dir, "docs") };
        var project = new ProjectService(app);
        project.Initialize();
        var s = project.Store;
        s.Insert(new Room { Building = Buildings.Branded, Level = "L02", Code = "P2-106", RoomType = "2BR", AreaType = "APARTMENT", Floor = 2 });
        s.Insert(new Room { Building = Buildings.Branded, Level = "L02", Code = "P2-107", RoomType = "2BR", AreaType = "APARTMENT", Floor = 2 });
        foreach (var (room, stage, item, qty) in new[]
                 {
                     ("P2-106", Stages.Second, "LIGHT", 39.0), ("P2-106", Stages.Second, "POWER", 36.0), ("P2-106", Stages.First, "LIGHT", 39.0),
                     ("P2-107", Stages.Second, "LIGHT", 39.0),
                 })
            s.Insert(new RoomQty { Building = Buildings.Branded, Room = room, Stage = stage, Item = item, Qty = qty, Source = "TEST" });
        var now = DateTime.Now;
        void Claim(string sub, int inv, string room, string stage, string item, double qty, DateTime at) =>
            s.Insert(new ClaimLine { Building = Buildings.Branded, Subcontractor = sub, InvoiceNo = inv, Room = room, Floor = "2", Stage = stage, Item = item, Qty = qty, EnteredAt = at, WirNo = "WIR-EL-0001" });
        Claim("ROOTS", 1, "P2-106", Stages.Second, "LIGHT", 20, now.AddDays(-10));
        Claim("ABRAG", 1, "P2-106", Stages.Second, "LIGHT", 5, now.AddDays(-9));
        Claim("ROOTS", 1, "P2-106", Stages.First, "LIGHT", 39, now.AddDays(-30));
        Claim("ROOTS", 2, "P2-107", Stages.Second, "LIGHT", 45, now.AddHours(-2));   // OVER the cap (39)
        s.Insert(new Contract { ContractNo = "SUB-TEST-001", Subcontractor = "ROOTS", Building = Buildings.Branded, Value = 1_000_000, RetentionPct = 0.10, SignedAt = new DateTime(2026, 5, 12) });
        s.Insert(new ContractItem { ContractNo = "SUB-TEST-001", ItemNo = "11", Order = 11, Description = "Lighting point 2nd fix pulling", Unit = "No.", Qty = 4000, Rate = 55, FixStage = Stages.Second, StagePct = 0.9 });
        s.Insert(new SubInvoice { ContractNo = "SUB-TEST-001", Subcontractor = "ROOTS", InvoiceNo = 1, Revision = 0, Status = SubInvoiceStatus.Rejected, RejectionReason = "WIR missing for L2", CreatedAt = now.AddDays(-5), AconexWorkflowNo = "WF-000123" });
        project.Reload();

        var mats = new SqliteMaterialsStore(() => (Raffaello.Core.Data.Db)project.Store);
        mats.EnsureSchema();
        var po = mats.Insert(new MatPo { PoNo = "RAF-P.O-E-045-2026", Supplier = "Riyadh Cables" });
        var dn = mats.Insert(new MatDn { DnNo = "81064344", Supplier = "Riyadh Cables", PoNo = "RAF-P.O-E-045-2026", DnDate = new DateTime(2026, 8, 15), OrderNo = "40222645" });
        mats.Insert(new MatDnLine { DnId = dn.Id, Order = 1, Description = "CU/XLPE 4x16", Batch = "0013289917", RawQty = 0.6, RawUnit = "KM", Qty = 600, Unit = "M" });
        _ = po;

        var store = new SqliteAssistantStore(app.DataFilePath, "tester");
        store.EnsureSchema();
        var data = new AssistantData
        {
            Project = project, Store = store, Settings = settings ?? new AssistantSettings(), Materials = () => mats.Load(),
            DraftFolder = Path.Combine(dir, "drafts"),
        };
        return new AssistantEnv { Project = project, Store = store, Materials = mats, Data = data, Dir = dir };
    }
}

/// <summary>Plays back scripted assistant turns and records every request (no network).</summary>
internal sealed class FakeTransport : IClaudeTransport
{
    private readonly Queue<Func<ClaudeRequest, ClaudeTurn>> _turns = new();
    public List<ClaudeRequest> Requests { get; } = new();

    public FakeTransport Then(Func<ClaudeRequest, ClaudeTurn> turn) { _turns.Enqueue(turn); return this; }

    public FakeTransport ThenTool(string name, JsonObject input, string id = "toolu_1", string preamble = "Let me check.") =>
        Then(_ => Turn("tool_use", new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig-" + id },
            new JsonObject { ["type"] = "text", ["text"] = preamble },
            new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }));

    public FakeTransport ThenText(string text) => Then(_ => Turn("end_turn", new JsonObject { ["type"] = "text", ["text"] = text }));

    public static ClaudeTurn Turn(string stop, params JsonObject[] blocks)
    {
        var t = new ClaudeTurn { StopReason = stop, Model = "claude-opus-5-5", InputTokens = 100, OutputTokens = 20 };
        foreach (var b in blocks) t.Content.Add(b);
        return t;
    }

    public Task<ClaudeTurn> SendAsync(ClaudeRequest request, Action<string>? onText, CancellationToken ct)
    {
        Requests.Add(request);
        var turn = _turns.Dequeue()(request);
        foreach (var b in turn.Content.OfType<JsonObject>().Where(b => (string?)b["type"] == "text")) onText?.Invoke((string?)b["text"] ?? "");
        return Task.FromResult(turn);
    }
}

internal static class Sse
{
    public static Stream Of(params object[] events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            var json = e is string s ? s : System.Text.Json.JsonSerializer.Serialize(e);
            sb.Append("event: x\n").Append("data: ").Append(json).Append("\n\n");
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
