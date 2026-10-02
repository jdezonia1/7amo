using Raffaello.Core.Domain;

namespace Raffaello.Core.AconexWeb;

/// <summary>[phase6] What an Aconex workflow result means for the linked invoice revision. Only proposals: the user confirms every change.</summary>
public sealed record OutcomeProposal(SubInvoice Invoice, string Action, string WorkflowNo, string Reason)
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public string Question => Action == Approve
        ? $"Aconex workflow {WorkflowNo} is APPROVED.\n\nMark {Invoice.Title} as APPROVED in Raffaello? It becomes the 'previous' for the next invoice and is locked."
        : $"Aconex workflow {WorkflowNo} is REJECTED{(Reason.Length > 0 ? $" ({Reason})" : "")}.\n\nRecord the rejection on {Invoice.Title}" +
          (InvoiceKinds.IsSubcontractor(Invoice) ? $" and prepare Rev {Invoice.Revision + 1} from the ledger?" : "?");
}

public static class AconexOutcomes
{
    /// <summary>The proposal for one workflow result, or null when there is nothing to do (no link, already recorded, still in progress).</summary>
    public static OutcomeProposal? Propose(WorkflowLookupResult r, IEnumerable<AconexWorkflowLink> links, IEnumerable<SubInvoice> invoices)
    {
        if (r.State is not (WorkflowStates.Approved or WorkflowStates.Rejected)) return null;
        var wf = WorkflowParser.NormalizeWf(r.WorkflowNo);
        var ids = links.Where(l => l.Active && WorkflowParser.NormalizeWf(l.WorkflowNo) == wf).Select(l => l.SubInvoiceId).ToHashSet();
        var inv = invoices.Where(i => ids.Contains(i.Id) || (i.AconexWorkflowNo.Length > 0 && WorkflowParser.NormalizeWf(i.AconexWorkflowNo) == wf))
            .OrderByDescending(i => i.Revision).FirstOrDefault();
        if (inv is null || inv.Locked) return null;
        if (r.State == WorkflowStates.Approved && inv.Status != SubInvoiceStatus.Approved) return new(inv, OutcomeProposal.Approve, r.WorkflowNo, "");
        if (r.State == WorkflowStates.Rejected && inv.Status != SubInvoiceStatus.Rejected)
        {
            var reason = r.Steps.Where(s => s.StepOutcome.Length > 0).Select(s => $"{s.StepName}: {s.StepOutcome}").LastOrDefault() ?? r.Outcome;
            return new(inv, OutcomeProposal.Reject, r.WorkflowNo, string.IsNullOrWhiteSpace(reason) ? "rejected in Aconex" : $"Aconex: {reason}");
        }
        return null;
    }
}
