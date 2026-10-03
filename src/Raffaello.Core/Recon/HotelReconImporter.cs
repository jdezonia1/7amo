using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Recon;

/// <summary>
/// HOTEL RECON - thin wrapper over <see cref="ReconImporter"/> with building HOTEL (kept for the recon-hotel CLI command and older callers).
/// </summary>
public static class HotelReconImporter
{
    public const string ClaimSource = ReconImporter.ClaimSource;
    public const string QtySource = ReconImporter.QtySource;
    public const string ProjectSheet = ReconImporter.ProjectSheet;
    public const string ClaimsSheet = ReconImporter.ClaimsSheet;

    public static ReconImportResult Read(string projectQtyPath, string cleanClaimsPath) =>
        ReconImporter.Read(Buildings.Hotel, projectQtyPath, cleanClaimsPath);

    /// <summary>Floor code to number: B2 = -2, B1 = -1, GF = 0, L1 = 1, L02 = 2, RF (roof) = 99; anything else 0.</summary>
    public static int ParseFloor(string floor) => ReconImporter.ParseFloor(floor);

    /// <summary>Replaces HOTEL PROJECT QTY and the HOTEL RECON claim lines (see <see cref="ReconImporter.Commit"/>).</summary>
    public static (int rooms, int qty, int claims, int deletedClaims) Commit(ReconImportResult res, IProjectStore store, bool replaceAllHotelClaims = false)
    {
        if (res.Building != Buildings.Hotel) throw new ArgumentException($"HotelReconImporter.Commit got a {res.Building} recon.");
        return ReconImporter.Commit(res, store, replaceAllHotelClaims);
    }
}