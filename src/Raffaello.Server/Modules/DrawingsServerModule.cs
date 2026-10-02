using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raffaello.Core.Drawings;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Modules;

/// <summary>
/// [drawings] Drawings module tables (sheets, symbol library, linear classes, takeoffs, hits, runs, calibrations, rooms, PROJECT QTY
/// decisions, statement checks, revision compares). PROJECT QTY decisions are an audit trail: they can be added, never edited.
/// </summary>
public sealed class DrawingsServerModule : IServerModule
{
    public string Name => "Drawings";
    public IEnumerable<Type> EntityTypes => DrawingEntities.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new DecisionAppendOnlyGuard() };

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        // a small look-up for dashboards: sheets with their latest takeoff
        api.MapGet("/api/v1/drawings/info", () => Results.Ok(new { Module = Name, Tables = DrawingEntities.All.Select(EntityMeta.TableOf).ToArray() }));
    }
}

public sealed class DecisionAppendOnlyGuard : IWriteGuard
{
    public void Check(WriteCheck c)
    {
        if (c.Type == typeof(DwgQtyDecision) && c.Kind == WriteKind.Update)
            throw new WriteRejectedException(409, ErrorCodes.AppendOnly, $"PROJECT QTY decision #{c.Entity.Id} is an audit record and cannot be changed - record a new decision.");
    }
}
