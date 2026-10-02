namespace Raffaello.Core.Chain;

/// <summary>The shared filter: Building, Level, Room, System, Stage. Null means ALL.</summary>
public sealed record FilterSpec(string? Building = null, string? Level = null, string? Room = null, string? System = null, string? Stage = null)
{
    public static readonly FilterSpec All = new();

    public bool Matches(string building, string level, string room, string system, string stage) =>
        (Building is null || Building == building) &&
        (Level is null || Level == level) &&
        (Room is null || Room == room) &&
        (System is null || System == system) &&
        (Stage is null || Stage == stage);

    public bool Matches(ChainRow r) => Matches(r.Building, r.Level, r.Room, r.System, r.Stage);

    public IEnumerable<ChainRow> Apply(IEnumerable<ChainRow> rows) => rows.Where(Matches);

    public string Describe()
    {
        var parts = new[] { Building, Level, Room, System, Stage }.Where(p => p is not null).ToList();
        return parts.Count == 0 ? "ALL BUILDINGS / ALL STAGES" : string.Join(" / ", parts);
    }
}
