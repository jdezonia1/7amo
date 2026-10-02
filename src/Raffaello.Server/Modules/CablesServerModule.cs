using Microsoft.AspNetCore.Routing;
using Raffaello.Core.Cables;
using Raffaello.Server.Data;

namespace Raffaello.Server.Modules;

/// <summary>
/// [cables] Panel &amp; cable register, cable claims, flag decisions and import profiles. No write guard: cable flags are warnings
/// (never blocking, bypassed with a reason) - the client computes them with the same <see cref="CableFlagEngine"/>.
/// </summary>
public sealed class CablesServerModule : IServerModule
{
    public string Name => "Cables";
    public IEnumerable<Type> EntityTypes => CableStore.EntityTypes;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => Array.Empty<IWriteGuard>();
    public void MapEndpoints(IEndpointRouteBuilder api) { }
}
