using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;

namespace Raffaello.App.Services;

/// <summary>
/// The one shared filter: Building / Level / Room / System / Stage. Every module reads <see cref="Spec"/>
/// and refreshes on <see cref="Changed"/>.
/// </summary>
public sealed partial class FilterState : ObservableObject
{
    public const string All = "ALL";
    private readonly DataService _data;
    private bool _suspend;

    public ObservableCollection<string> Buildings { get; } = new();
    public ObservableCollection<string> Levels { get; } = new();
    public ObservableCollection<string> Rooms { get; } = new();
    public ObservableCollection<string> Systems { get; } = new();
    public ObservableCollection<string> Stages { get; } = new();

    [ObservableProperty] private string _building = All;
    [ObservableProperty] private string _level = All;
    [ObservableProperty] private string _room = All;
    [ObservableProperty] private string _system = All;
    [ObservableProperty] private string _stage = All;

    public event Action? Changed;

    public FilterState(DataService data)
    {
        _data = data;
        _data.DataChanged += RebuildOptions;
    }

    public FilterSpec Spec => new(N(Building), N(Level), N(Room), N(System), N(Stage));
    private static string? N(string v) => v == All || string.IsNullOrEmpty(v) ? null : v;

    public string Description => Spec.Describe();
    public bool IsFiltered => Spec != FilterSpec.All;

    partial void OnBuildingChanged(string value) { RebuildRooms(); Raise(); }
    partial void OnLevelChanged(string value) { RebuildRooms(); Raise(); }
    partial void OnRoomChanged(string value) => Raise();
    partial void OnSystemChanged(string value) => Raise();
    partial void OnStageChanged(string value) => Raise();

    private void Raise()
    {
        if (_suspend) return;
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(IsFiltered));
        Changed?.Invoke();
    }

    public void Set(string? building = null, string? level = null, string? room = null, string? system = null, string? stage = null)
    {
        _suspend = true;
        Building = building ?? All;
        Level = level ?? All;
        RebuildRooms();
        Room = room ?? All;
        System = system ?? All;
        Stage = stage ?? All;
        _suspend = false;
        Raise();
    }

    public void Clear() => Set();

    public void RebuildOptions()
    {
        var lines = _data.Project.Snapshot.Lines;
        // [phase6] BRANDED and HOTEL always, plus any building of the rooms / demo lines
        Sync(Buildings, new[] { All }.Concat(lines.Select(l => l.Building).Concat(_data.Project.Snapshot.Rooms.Select(r => r.Building))
            .Concat(new[] { Raffaello.Core.Domain.Buildings.Branded, Raffaello.Core.Domain.Buildings.Hotel }).Where(b => !string.IsNullOrEmpty(b)).Distinct().OrderBy(x => x)));
        Sync(Levels, new[] { All }.Concat(lines.Select(l => l.Level).Distinct().OrderBy(ProjectAnalytics.LevelRank)));
        Sync(Systems, new[] { All }.Concat(lines.Select(l => l.System).Distinct().OrderBy(s => Array.IndexOf(Raffaello.Core.Domain.Systems.Main, s) is var i && i < 0 ? 99 : i)));
        Sync(Stages, new[] { All }.Concat(Raffaello.Core.Domain.Stages.All));
        RebuildRooms();
    }

    private void RebuildRooms()
    {
        var spec = new FilterSpec(N(Building), N(Level));
        var rooms = _data.Project.Snapshot.Lines.Where(l => spec.Matches(l.Building, l.Level, l.Room, l.System, l.Stage))
            .Select(l => l.Room).Distinct().OrderBy(r => r, StringComparer.OrdinalIgnoreCase);
        Sync(Rooms, new[] { All }.Concat(rooms));
        if (!Rooms.Contains(Room))
        {
            var was = _suspend;
            _suspend = true;
            Room = All;
            _suspend = was;
        }
    }

    private static void Sync(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var i in list) target.Add(i);
    }
}
