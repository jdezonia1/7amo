using Microsoft.AspNetCore.Routing;
using Raffaello.Core.Insights;
using Raffaello.Server.Data;

namespace Raffaello.Server.Modules;

/// <summary>[insights] Insights tables: dismissed warnings (with reasons), thresholds, consumption norms, programme, payment terms,
/// invoice period dates and the document-hash cache. Plain CRUD with RowVersion checks, audit and change notices.</summary>
public sealed class InsightsServerModule : IServerModule
{
    public string Name => "Insights";
    public IEnumerable<Type> EntityTypes => InsightsEntities.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new DismissalReasonGuard() };
    public void MapEndpoints(IEndpointRouteBuilder api) { }
}

/// <summary>A warning can only be dismissed with a reason (the same rule as the desktop store).</summary>
public sealed class DismissalReasonGuard : IWriteGuard
{
    public void Check(WriteCheck c)
    {
        if (c.Type != typeof(InsightDismissal) || c.Kind == WriteKind.Delete) return;
        var d = (InsightDismissal)c.Entity;
        if (d.Active && string.IsNullOrWhiteSpace(d.Reason))
            throw new WriteRejectedException(422, "reason_required", "Give a reason for dismissing the warning (it is recorded).");
    }
}
