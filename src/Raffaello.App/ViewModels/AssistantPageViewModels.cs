using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.App.Services.Assistant;
using Raffaello.Core.Ai;
using Raffaello.Core.Assistant;
using Raffaello.Core.Localization;
using Raffaello.Core.Notify;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

/// <summary>[assistant] ASK RAFFAELLO as a full page (same conversation as the Ctrl+Shift+A panel).</summary>
public sealed class AssistantPageViewModel : PageViewModel
{
    public AssistantPageViewModel(PageContext ctx, AskViewModel ask) : base(ctx) => Ask = ask;

    public AskViewModel Ask { get; }
    public override string Key => "Assistant";
    public override string Title => "ASK RAFFAELLO";
    public override string Subtitle => "Ask about rooms, the ledger, invoices, deliveries and today's work - answers cite their sources";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    protected override void Refresh()
    {
        Ask.IsOpen = false;
        Ask.IsPageMode = true;
        Ask.Open();
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { Length: > 0 } q) Ask.Open(q);
    }

    /// <summary>Leaving the page: the chat goes back to slide-in mode.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsActive) && !IsActive) Ask.IsPageMode = false;
        if (e.PropertyName == nameof(IsActive) && IsActive) { Ask.IsOpen = false; Ask.IsPageMode = true; }
    }
}

public sealed partial class BriefSectionViewModel : ObservableObject
{
    public required BriefSection Section { get; init; }
    public required string Lang { get; init; }
    public string Title => Section.Title(Lang);
    public string Count => Section.Count.ToString(Loc.Culture(Lang));
    public string Delta => Section.Delta is int d && d != 0 ? (d > 0 ? "+" : "") + d.ToString(Loc.Culture(Lang)) : "";
    public string Tag => Section.Items.Any(i => i.Severity == "OVER") ? "OVER" : Section.Items.Any(i => i.Severity is "CHECK") ? "CHECK" : Section.Count > 0 ? "DUE" : "OK";
    public IReadOnlyList<BriefItem> Items => Section.Items.Take(12).ToList();
    public string More => Section.Items.Count > 12 ? $"+{Section.Items.Count - 12}" : "";
    public bool IsEmpty => Section.Items.Count == 0;
}

/// <summary>
/// [assistant] MORNING BRIEF: what changed since the previous brief and what needs the user today; the first screen of the day
/// (Settings), PDF export, send now through the user's channels, and an optional short summary written by Claude.
/// </summary>
public sealed partial class BriefViewModel : PageViewModel
{
    private readonly AssistantHost _host;
    private readonly AssistantNotificationService _notify;

    public BriefViewModel(PageContext ctx, AssistantHost host, AssistantNotificationService notify) : base(ctx)
    {
        _host = host;
        _notify = notify;
    }

    public override string Key => "Brief";
    public override string Title => "MORNING BRIEF";
    public override string Subtitle => "What changed since yesterday and what needs you today";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public ObservableCollection<BriefSectionViewModel> Sections { get; } = new();
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _since = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    public Brief? Current { get; private set; }

    protected override void Refresh()
    {
        var p = Project;
        var lang = _host.Settings.Language;
        var brief = _notify.Hub.BuildBrief(_host.Data.User, p.Snapshot, p.Queue, lang, p.Settings.WirDueDays);
        if (Current?.Summary is { Length: > 0 } s && Current.Date == brief.Date) brief.Summary = s;
        Current = brief;
        var c = Loc.Culture(lang);
        Header = $"{brief.Date.ToString("dddd dd MMMM yyyy", c)}  |  {brief.Owner}";
        Since = $"{Loc.T("Brief_Since")} {brief.Since.ToString("dd MMM HH:mm", c)}";
        Summary = brief.Summary;
        Sections.Clear();
        foreach (var sec in brief.Sections) Sections.Add(new BriefSectionViewModel { Section = sec, Lang = lang });
        try { MorningBriefBuilder.Save(_host.Store, brief); } catch (Exception ex) { Status = ex.Message; }
        _host.Settings.LastBriefShown = DateTime.Now;
        try { _host.SaveSettings(); } catch (Exception) { /* settings file busy: shown again next start */ }
    }

    [RelayCommand]
    private void OpenItem(BriefItem? i)
    {
        if (i is null || i.Module.Length == 0) return;
        Ctx.Nav.Go(i.Module, new NavTarget(i.Module, Key: i.Key.Length > 0 ? i.Key : null));
    }

    [RelayCommand]
    private void ExportPdf()
    {
        if (Current is null) return;
        var path = Ctx.Dialogs.SaveFile("Morning brief", $"Raffaello_brief_{Current.Date:yyyyMMdd}.pdf", "PDF|*.pdf");
        if (path is null) return;
        try
        {
            BriefPdf.Export(path, Current);
            Ctx.Toasts.Show(Loc.T("Toast_Exported"), System.IO.Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (Exception ex) { Ctx.Toasts.Show(Loc.T("Toast_SaveFailed"), ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task SendNow()
    {
        if (Current is null) return;
        IsBusy = true;
        try
        {
            var res = await _notify.Hub.SendBriefAsync(Current);
            Status = res.Count == 0 ? "Already sent today, or no channel rule for the brief (Settings > Notifications)."
                : string.Join("  |  ", res.Select(r => r.Sent ? $"{r.Channel}: OK" : $"{r.Channel}: {r.Error}"));
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Summarise()
    {
        if (Current is null) return;
        var key = _host.Key;
        if (string.IsNullOrWhiteSpace(key)) { Status = Loc.T("Ask_KeyMissing"); return; }
        IsBusy = true;
        try
        {
            var client = new AnthropicClient(key) { Model = _host.Session.Model, Effort = "low", MaxTokens = 2000, UseServerFallbacks = _host.Session.UseServerFallbacks };
            var text = await client.CompleteAsync("You summarise construction QS figures for a busy engineer. Use only the figures given.",
                new[] { new ChatMessage("user", MorningBriefBuilder.SummaryPrompt(Current)) });
            Current.Summary = text.Trim();
            Summary = Current.Summary;
            MorningBriefBuilder.Save(_host.Store, Current);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void RefreshBrief() => ForceRefresh();
}
