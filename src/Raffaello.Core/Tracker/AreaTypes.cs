namespace Raffaello.Core.Tracker;

/// <summary>Room area types. They choose the lighting BOQ row ("lighting points, to apartment / BOH / FOH / balcony").</summary>
public static class AreaTypes
{
    public const string Apartment = "APARTMENT";
    public const string Boh = "BOH";
    public const string Foh = "FOH";
    public const string Balcony = "BALCONY";
    public const string Facade = "FACADE";
    public const string Guestroom = "GUESTROOM";
    public const string Parking = "PARKING";

    public static readonly string[] All = { Apartment, Boh, Foh, Balcony, Facade, Guestroom, Parking };

    /// <summary>
    /// Default guess from the tracker: apartment unit types (1BR..4BRDX, villas V-1/V-2) are APARTMENT; basement and roof
    /// public areas are BOH; other public / common areas are FOH. Always editable per room.
    /// </summary>
    public static string Guess(string unitType, string level, string location)
    {
        var u = (unitType ?? "").Trim().ToUpperInvariant();
        var l = (level ?? "").Trim().ToUpperInvariant();
        var loc = (location ?? "").Trim().ToUpperInvariant();
        if (u.Contains("BR") || u.StartsWith("(V") || u.StartsWith("V-") || u.Contains("VILLA")) return Apartment;
        if (u.Contains("BALCON") || loc.Contains("BALC") || loc.Contains("TERR")) return Balcony;
        if (u.Contains("PARK") || loc.Contains("PARK")) return Parking;
        if (l.StartsWith("BASEMENT") || loc.Contains("BS") || l.StartsWith("ROOF") || loc.EndsWith("-RF") || u.Contains("PLANT") || u.Contains("BOH")) return Boh;
        if (u.Contains("PUBLIC") || u.Contains("LOBBY") || u.Contains("FOH")) return Foh;
        if (u.Contains("GUEST") || u.StartsWith("ER-")) return Guestroom;
        return Apartment;
    }

    /// <summary>[phase6] Building-aware guess: in the HOTEL a key / guest room (KING, TWIN, SUITE, room numbers) is GUESTROOM, not APARTMENT.</summary>
    public static string GuessFor(string building, string unitType, string level, string location)
    {
        var g = Guess(unitType, level, location);
        if (!string.Equals(building, Raffaello.Core.Domain.Buildings.Hotel, StringComparison.OrdinalIgnoreCase) || g != Apartment) return g;
        var u = (unitType ?? "").ToUpperInvariant();
        if (u.Contains("RESIDENCE") || u.Contains("APARTMENT")) return Apartment;
        if (u.Contains("CORRIDOR") || u.Contains("LOBBY") || u.Contains("RESTAURANT") || u.Contains("BALLROOM") || u.Contains("SPA") || u.Contains("POOL") || u.Contains("GYM") || u.Contains("BAR")) return Foh;
        if (u.Contains("KITCHEN") || u.Contains("STORE") || u.Contains("LAUNDRY") || u.Contains("OFFICE") || u.Contains("STAFF") || u.Contains("ELEC") || u.Contains("MEP")) return Boh;
        return Guestroom;
    }
}
