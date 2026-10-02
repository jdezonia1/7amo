using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Modules;

/// <summary>
/// [assemblies] BOQ item breakdown tables (templates, parameters, components, prices, edited item specs, settings) with a guard that
/// keeps template codes unique and an offline endpoint that parses a description (no data access).
/// </summary>
public sealed class AssembliesServerModule : IServerModule
{
    public string Name => "Assemblies";
    public IEnumerable<Type> EntityTypes => AssemblyEntities.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new UniqueTemplateCodeGuard() };

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        api.MapPost("/api/v1/assemblies/parse", (ParseRequest r) => Results.Ok(ItemParser.Parse(r.Description, r.Unit,
            r.Source?.ToUpperInvariant() switch { "CONTRACT" => ItemSourceKind.Contract, "BOQ" => ItemSourceKind.Boq, _ => ItemSourceKind.Text })));
    }

    public sealed record ParseRequest(string Description, string? Unit, string? Source);
}

/// <summary>Two users creating a template with the same code at the same moment: one wins, the other gets 409.</summary>
public sealed class UniqueTemplateCodeGuard : IWriteGuard
{
    public const string DuplicateCode = "duplicate";

    public void Check(WriteCheck c)
    {
        if (c.Type != typeof(AsmTemplate) || c.Kind == WriteKind.Delete) return;
        var t = (AsmTemplate)c.Entity;
        var code = (t.Code ?? "").Trim().ToUpperInvariant();
        c.Tx.Lock("asmtemplate|" + code);
        var same = c.Tx.Query<AsmTemplate>($"""SELECT * FROM {PgMap.Q("AsmTemplates")} WHERE UPPER("Code") = @c AND "Id" <> @id""", ("c", code), ("id", t.Id));
        if (same.Count > 0)
            throw new WriteRejectedException(409, DuplicateCode, $"Template code {code} already exists (changed by {same[0].UpdatedBy}).");
    }
}
