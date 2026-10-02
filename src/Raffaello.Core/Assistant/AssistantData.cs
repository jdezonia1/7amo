using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Assistant;

/// <summary>Where the assistant reads from. Module stores are optional (a tool that needs a missing one says so).</summary>
public sealed class AssistantData
{
    public required ProjectService Project { get; init; }
    public required IAssistantStore Store { get; init; }
    public required AssistantSettings Settings { get; init; }
    public Func<MaterialsSnapshot?>? Materials { get; init; }
    public Func<IAconexStore?>? Aconex { get; init; }
    public Func<IVariationStore?>? Variations { get; init; }
    /// <summary>Full-text document archive, when the documents module provides one; records are searched either way.</summary>
    public IDocumentSearch? Documents { get; init; }
    /// <summary>Claim anomaly detection, when a module provides it (the Insights engine, see <see cref="Wiring.InsightsAnomalies"/>);
    /// when it is missing or fails, the built-in checks run.</summary>
    public Func<IEnumerable<AnomalyItem>>? Anomalies { get; init; }
    /// <summary>Contract intelligence (terms, rules, bypasses read from the signed contracts) - feeds the obligations calendar in
    /// get_contract_terms. Null = not available (the contract tables are read from the project data only).</summary>
    public Func<Raffaello.Core.Documents.IDocumentStore?>? ContractDocs { get; init; }
    /// <summary>Folder for drafts the assistant writes (e-mails). Empty = Documents\Raffaello\Assistant.</summary>
    public string DraftFolder { get; init; } = "";
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    /// <summary>The signed-in user as the data source knows him (server account name in server mode) - owner of conversations / rules.</summary>
    public string User => string.IsNullOrWhiteSpace(Project.CurrentUser) ? Project.Settings.EffectiveUserName : Project.CurrentUser;

    public MaterialsSnapshot? TryMaterials() { try { return Materials?.Invoke(); } catch (Exception) { return null; } }
    public IAconexStore? TryAconex() { try { return Aconex?.Invoke(); } catch (Exception) { return null; } }
    public IVariationStore? TryVariations() { try { return Variations?.Invoke(); } catch (Exception) { return null; } }
    public Raffaello.Core.Documents.IDocumentStore? TryContractDocs() { try { return ContractDocs?.Invoke(); } catch (Exception) { return null; } }

    public string DraftsPath()
    {
        if (DraftFolder.Length > 0) return DraftFolder;
        var root = Raffaello.Core.Settings.ProjectFolders.From(Project.Settings);
        return Path.Combine(root.Root, "Assistant");
    }
}

/// <summary>What the user is looking at (screen, filter, selected record) - sent with each question.</summary>
public sealed record ScreenContext(string Screen = "", string Filter = "", string SelectedLine = "", string SelectedRecord = "", string? Building = null)
{
    public string Describe()
    {
        var parts = new List<string>();
        if (Screen.Length > 0) parts.Add($"screen: {Screen}");
        if (Building is { Length: > 0 }) parts.Add($"building: {Building}");
        if (Filter.Length > 0) parts.Add($"filter: {Filter}");
        if (SelectedLine.Length > 0) parts.Add($"selected line: {SelectedLine}");
        if (SelectedRecord.Length > 0) parts.Add($"selected record: {SelectedRecord}");
        return parts.Count == 0 ? "no screen context" : string.Join("; ", parts);
    }
}

public sealed record DocumentHit(string Id, string Title, string Path, int Page, string Snippet, string Kind)
{
    /// <summary>The record the document fed (archive hits): table / key as stored on the evidence index (e.g. "Contract" + contract no).</summary>
    public string LinkedTable { get; init; } = "";
    public string LinkedKey { get; init; } = "";
    public string DocType { get; init; } = "";
}

/// <summary>Full-text search over stored documents (the documents module can provide a real archive).</summary>
public interface IDocumentSearch
{
    string Name { get; }
    IReadOnlyList<DocumentHit> Search(string query, int limit);
}

public sealed record AnomalyItem(string Severity, string Kind, string Title, string Detail, Citation? Source)
{
    /// <summary>Plain-English explanation (Insights engine).</summary>
    public string Explanation { get; init; } = "";
    public string SuggestedAction { get; init; } = "";
    /// <summary>The records that show the anomaly (ledger lines, invoices, WIRs, documents ...), as citations.</summary>
    public List<Citation> Evidence { get; init; } = new();
    public string Subcontractor { get; init; } = "";
    public int InvoiceNo { get; init; }
}
