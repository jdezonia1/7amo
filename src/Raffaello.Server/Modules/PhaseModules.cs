using Microsoft.AspNetCore.Routing;
using Raffaello.Core.Materials;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;
using static Raffaello.Server.Data.PgMap;

namespace Raffaello.Server.Modules;

/// <summary>[phase6] Phase-3 tables (materials, supplier-invoice DN locks, owner MOS, BOQ, coding memory) with the DN-line hard lock.</summary>
public sealed class MaterialsServerModule : IServerModule
{
    public string Name => "Materials";
    public IEnumerable<Type> EntityTypes => MaterialsStoreBase.EntityTypes;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new DnLineLockGuard() };
    public void MapEndpoints(IEndpointRouteBuilder api) { }
}

/// <summary>[phase6] Aconex workflow links / checks / history / downloads.</summary>
public sealed class AconexServerModule : IServerModule
{
    public string Name => "Aconex";
    public IEnumerable<Type> EntityTypes => ModuleEntities.Aconex;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => Array.Empty<IWriteGuard>();
    public void MapEndpoints(IEndpointRouteBuilder api) { }
}

/// <summary>[phase6] Variations / EI register.</summary>
public sealed class VariationsServerModule : IServerModule
{
    public string Name => "Variations";
    public IEnumerable<Type> EntityTypes => ModuleEntities.Variations;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => Array.Empty<IWriteGuard>();
    public void MapEndpoints(IEndpointRouteBuilder api) { }
}

/// <summary>
/// A DN line can be invoiced once: inserting (or re-pointing) a <see cref="MatDnInvoiceLock"/> takes a transaction lock on the
/// DN line and refuses when another invoice already holds it. Two users invoicing the same DN line at the same moment: one wins,
/// the other gets 409 "locked".
/// </summary>
public sealed class DnLineLockGuard : IWriteGuard
{
    public void Check(WriteCheck c)
    {
        if (c.Type != typeof(MatDnInvoiceLock) || c.Kind == WriteKind.Delete) return;
        var k = (MatDnInvoiceLock)c.Entity;
        c.Tx.Lock("dnline|" + k.DnLineId);
        var held = c.Tx.Query<MatDnInvoiceLock>($"""SELECT * FROM {Q("MatDnInvoiceLocks")} WHERE "DnLineId" = @d AND "Id" <> @id""", ("d", k.DnLineId), ("id", k.Id));
        var other = held.FirstOrDefault(h => !(h.InvoiceNo == k.InvoiceNo && string.Equals(h.Supplier, k.Supplier, StringComparison.OrdinalIgnoreCase)
                                                && MaterialsSnapshot.PoKey(h.PoNo) == MaterialsSnapshot.PoKey(k.PoNo)));
        if (other != null)
            throw new WriteRejectedException(409, ErrorCodes.Locked,
                $"DN line #{k.DnLineId} is already invoiced under {other.Supplier} {other.PoNo} INV-{other.InvoiceNo:00}. A DN line can be invoiced once.");
        if (held.Count > 0 && c.Kind == WriteKind.Insert)
            throw new WriteRejectedException(409, ErrorCodes.Locked, $"DN line #{k.DnLineId} is already locked to this invoice.");
    }
}
