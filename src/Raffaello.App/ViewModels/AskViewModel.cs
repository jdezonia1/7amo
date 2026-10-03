using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.App.Services.Assistant;
using Raffaello.Core.Assistant;
using Raffaello.Core.Localization;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

/// <summary>A proposed write shown as a card in the chat: nothing happens until CONFIRM.</summary>
public sealed partial class ActionCardViewModel : ObservableObject
{
    public ActionCardViewModel(AssistantAction a) => Update(a);
    public long Id { get; private set; }
    public string Kind { get; private set; } = "";
    public string Title => AssistantActionKinds.Label(Kind);
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _resultRef = "";
    public bool CanDecide => Status == AssistantActionStatus.Proposed;
    public string StatusTag => Status switch
    {
        AssistantActionStatus.Executed => "OK", AssistantActionStatus.Failed => "OVER", AssistantActionStatus.Cancelled => "OPEN", _ => "CHECK",
    };

    public void Update(AssistantAction a)
    {
        Id = a.Id; Kind = a.Kind;
        Preview = a.Preview; Warnings = a.Warnings; Status = a.Status; Result = a.Result; ResultRef = a.ResultRef;
        OnPropertyChanged(nameof(CanDecide));
        OnPropertyChanged(nameof(StatusTag));
        OnPropertyChanged(nameof(Title));
    }
}

/// <summary>One bubble: a question, or an answer with its sources (chips) and cards.</summary>
public sealed partial class AskMessage : ObservableObject
{
    public string Role { get; init; } = "user";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _activity = "";
    [ObservableProperty] private bool _offline;
    public bool IsUser => Role == "user";
    public ObservableCollection<Citation> Citations { get; } = new();
    public ObservableCollection<ActionCardViewModel> Cards { get; } = new();
    public FlowDirection Direction => OfflineAssistant.IsArabic(Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(Direction));
}

public sealed record AttachmentChip(string Path, ChatAttachment Read)
{
    public string Label => Read.Summary;
}

/// <summary>
/// "Ask Raffaello": chat with Claude over the app's data (Ctrl+Shift+A slide-in, also a full page). Streams answers, cites the records
/// it used (chips navigate in the app), proposes writes as cards that run only on CONFIRM, reads attached files, keeps the history per
/// user, and answers simple questions offline from local data when there is no key or no connection.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    private readonly DataService _data;
    private readonly FilterState _filter;
    private readonly SelectionService _selection;
    private readonly AssistantHost _host;
    private readonly ToastService _toasts;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _cts;
    private readonly INavigator _navigator;

    public AskViewModel(DataService data, FilterState filter, SelectionService selection, AssistantHost host, ToastService toasts, DialogService dialogs, NavigatorProxy navigator)
    {
        _data = data; _filter = filter; _selection = selection; _host = host; _toasts = toasts; _dialogs = dialogs; _navigator = navigator;
        _selection.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ContextText));
        _filter.Changed += () => OnPropertyChanged(nameof(ContextText));
        Loc.LanguageChanged += () => { OnPropertyChanged(nameof(Suggestions)); RefreshStatus(); };
        RefreshStatus();
    }

    public ObservableCollection<AskMessage> Messages { get; } = new();
    public ObservableCollection<AssistantConversation> Conversations { get; } = new();
    public ObservableCollection<AttachmentChip> Attachments { get; } = new();

    public string[] Suggestions => new[] { "Ask_Suggest1", "Ask_Suggest2", "Ask_Suggest3", "Ask_Suggest4", "Ask_Suggest5", "Ask_Suggest6" }.Select(Loc.T).ToArray();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isPageMode;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _privacyText = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _activity = "";

    public string ContextText => _selection.SelectedLine is { } r
        ? $"{Loc.T("Ask_Context")}: {r.Key}  |  {r.Status}"
        : $"{Loc.T("Ask_Context")}: {TrScreen(_selection.Screen)}  |  {_filter.Description}";

    private static string TrScreen(string s) => Loc.FromEnglish(s) ?? s;

    public ScreenContext Screen() => new(
        _selection.Screen,
        _filter.Description,
        _selection.SelectedLine is { } r ? $"{r.Key} ({r.Status}): {string.Join("; ", r.Findings.Select(f => f.Message).Take(3))}" : "",
        // [assistant] pages that set SelectionService.SelectedRecord tell the assistant what is selected
        _selection.SelectedRecord,
        _filter.Spec.Building);

    public void RefreshStatus()
    {
        _host.ApplyModel();
        var route = _host.Route();
        IsOnline = route.Route != AssistantRoute.Offline;
        ModeText = route.Route switch
        {
            // [claude-login] answers through Claude Code with the user's own Claude login
            AssistantRoute.ClaudeCode => $"CLAUDE LOGIN (Claude Code)  |  {(_host.Settings.ClaudeCodeModel.Trim().Length > 0 ? _host.Settings.ClaudeCodeModel.Trim() : "plan default model")}  |  read-only",
            AssistantRoute.Api => $"{Loc.T("Ask_Online")}  |  {_host.Session.Model}  |  effort {_host.Session.Effort}",
            _ => Loc.T("Ask_Offline") + (route.Warn ? "  |  " + route.Reason : ""),
        };
        PrivacyText = _host.Session.PrivacySummary(Attachments.Select(a => a.Read).ToList());
    }

    public void Open(string? question = null)
    {
        if (!IsPageMode) IsOpen = true;
        RefreshStatus();
        OnPropertyChanged(nameof(ContextText));
        if (Messages.Count == 0 && _host.Session.Conversation is null) LoadConversations();
        if (!string.IsNullOrWhiteSpace(question)) { Input = question; _ = Send(); }
    }

    [RelayCommand] private void Close() { IsOpen = false; }

    [RelayCommand]
    private void OpenFullPage()
    {
        IsOpen = false;
        _navigator.Go("Assistant");
    }

    [RelayCommand]
    private void NewConversation()
    {
        _cts?.Cancel();
        _host.Session.NewConversation();
        Messages.Clear();
        Attachments.Clear();
        ShowHistory = false;
        RefreshStatus();
    }

    // the old binding name in older layouts
    [RelayCommand] private void Clear() => NewConversation();

    [RelayCommand]
    private void ToggleHistory()
    {
        ShowHistory = !ShowHistory;
        if (ShowHistory) LoadConversations();
    }

    private void LoadConversations()
    {
        try
        {
            Conversations.Clear();
            foreach (var c in _host.Session.Conversations().Take(50)) Conversations.Add(c);
        }
        catch (Exception ex) { _toasts.Show("HISTORY", ex.Message, ToastKind.Warn); }
    }

    [RelayCommand]
    private void OpenConversation(AssistantConversation? c)
    {
        if (c is null) return;
        try
        {
            _host.Session.Open(c.Id);
            Messages.Clear();
            var cards = _host.Session.ConversationActions().GroupBy(a => a.ConversationId).SelectMany(g => g).ToList();
            foreach (var b in _host.Session.Bubbles())
            {
                var m = new AskMessage { Role = b.Role, Text = b.Text, Offline = b.Offline };
                foreach (var ci in b.Citations) m.Citations.Add(ci);
                Messages.Add(m);
            }
            // cards go under the last assistant bubble of the conversation (in order)
            var last = Messages.LastOrDefault(m => !m.IsUser);
            if (last != null) foreach (var a in cards) last.Cards.Add(new ActionCardViewModel(a));
            ShowHistory = false;
        }
        catch (Exception ex) { _toasts.Show("HISTORY", ex.Message, ToastKind.Warn); }
    }

    [RelayCommand]
    private void ArchiveConversation(AssistantConversation? c)
    {
        if (c is null) return;
        try
        {
            _host.Session.Archive(c);
            Conversations.Remove(c);
            if (_host.Session.Conversation is null) Messages.Clear();
        }
        catch (Exception ex) { _toasts.Show("HISTORY", ex.Message, ToastKind.Warn); }
    }

    [RelayCommand] private Task UseSuggestion(string? s) { Input = s ?? ""; return Send(); }

    [RelayCommand]
    private async Task Attach()
    {
        var files = _dialogs.OpenFiles("Attach to the question", AttachmentReader.FileFilter);
        if (files is null) return;
        var reader = _host.AttachmentReader();
        foreach (var f in files)
        {
            try
            {
                var read = await reader.ReadAsync(f);
                Attachments.Add(new AttachmentChip(f, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _toasts.Show("ATTACH", ex.Message, ToastKind.Warn); }
        }
        RefreshStatus();
    }

    [RelayCommand] private void RemoveAttachment(AttachmentChip? a) { if (a != null) Attachments.Remove(a); RefreshStatus(); }

    [RelayCommand]
    private async Task Send()
    {
        var text = Input.Trim();
        if ((text.Length == 0 && Attachments.Count == 0) || IsBusy) return;
        Input = "";
        var attachments = Attachments.Select(a => a.Read).ToList();
        Messages.Add(new AskMessage { Role = "user", Text = text.Length > 0 ? text : string.Join(", ", attachments.Select(a => a.FileName)) });
        Attachments.Clear();
        var answer = new AskMessage { Role = "assistant", Text = "", Activity = Loc.T("Ask_Thinking") };
        Messages.Add(answer);
        IsBusy = true;
        RefreshStatus();
        _cts = new CancellationTokenSource();
        var ui = Application.Current?.Dispatcher;
        void OnUi(Action a) { if (ui is null || ui.CheckAccess()) a(); else ui.BeginInvoke(a); }
        try
        {
            var reply = await Task.Run(() => _host.Session.AskAsync(text, Screen(), attachments, e => OnUi(() =>
            {
                switch (e.Kind)
                {
                    case AssistantEventKind.TextDelta: answer.Text += e.Text; answer.Activity = ""; break;
                    case AssistantEventKind.ToolStarted: answer.Activity = $"{Loc.T("Ask_Reading")}: {e.Text.Replace('_', ' ')}..."; break;
                    case AssistantEventKind.ToolFinished: answer.Activity = ""; break;
                    case AssistantEventKind.ActionProposed when e.Action != null: answer.Cards.Add(new ActionCardViewModel(e.Action)); break;
                }
            }), _cts.Token));
            answer.Text = reply.Text.Length > 0 ? reply.Text : answer.Text;
            answer.Offline = reply.Offline;
            answer.Citations.Clear();
            foreach (var c in reply.Citations) answer.Citations.Add(c);
            if (reply.ToolsUsed.Contains("draft_ledger_claim") || reply.Actions.Count > 0) { /* cards already added */ }
        }
        catch (OperationCanceledException) { answer.Text += " [" + Loc.T("Btn_Stop") + "]"; }
        catch (Exception ex)
        {
            App.Log(ex);
            answer.Text += (answer.Text.Length > 0 ? "\n\n" : "") + ex.Message;
        }
        finally
        {
            answer.Activity = "";
            IsBusy = false;
            RefreshStatus();
        }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task ConfirmAction(ActionCardViewModel? card)
    {
        if (card is null || !card.CanDecide) return;
        try
        {
            var done = await Task.Run(() => _host.Session.Confirm(card.Id));
            card.Update(done);
            _data.RaiseChanged();
            _toasts.Show(card.Title, done.Result, done.Status == AssistantActionStatus.Executed ? ToastKind.Good : ToastKind.Error, 6);
            if (done.ResultRef.StartsWith("file|", StringComparison.Ordinal)) DialogService.OpenWithShell(done.ResultRef[5..]);
        }
        catch (Exception ex) { _toasts.Show(card.Title, ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void CancelAction(ActionCardViewModel? card)
    {
        if (card is null || !card.CanDecide) return;
        try { card.Update(_host.Session.Cancel(card.Id)); }
        catch (Exception ex) { _toasts.Show(card.Title, ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void OpenResult(ActionCardViewModel? card)
    {
        if (card is null || card.ResultRef.Length == 0) return;
        if (card.ResultRef.StartsWith("file|", StringComparison.Ordinal)) { DialogService.OpenWithShell(card.ResultRef[5..]); return; }
        var parts = card.ResultRef.Split('|', 2);
        Navigate(parts[0], parts.Length > 1 ? parts[1] : null);
    }

    [RelayCommand]
    private void OpenCitation(Citation? c)
    {
        if (c is null) return;
        if (c.Path.Length > 0 && File.Exists(c.Path)) { DialogService.OpenWithShell(c.Path); return; }
        if (c.Module.Length > 0) Navigate(c.Module, c.NavKey.Length > 0 ? c.NavKey : null);
    }

    private void Navigate(string module, string? key)
    {
        if (!IsPageMode) IsOpen = false;
        _navigator.Go(module, new NavTarget(module, Key: key));
    }
}
