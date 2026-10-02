using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Ai;

namespace Raffaello.App.ViewModels;

public sealed partial class AskMessage : ObservableObject
{
    public string Role { get; init; } = "user";
    [ObservableProperty] private string _text = "";
    public bool IsUser => Role == "user";
}

/// <summary>"Ask Raffaello": slide-in assistant that knows the selected line's chain and the current filter.</summary>
public sealed partial class AskViewModel : ObservableObject
{
    private readonly DataService _data;
    private readonly FilterState _filter;
    private readonly SelectionService _selection;
    private CancellationTokenSource? _cts;

    public AskViewModel(DataService data, FilterState filter, SelectionService selection)
    {
        _data = data; _filter = filter; _selection = selection;
        _selection.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ContextText));
        _filter.Changed += () => OnPropertyChanged(nameof(ContextText));
    }

    public ObservableCollection<AskMessage> Messages { get; } = new();
    public string[] Suggestions { get; } =
    {
        "Why is this line flagged?",
        "What can I certify on this line, and why?",
        "Summarise what needs me today.",
        "Draft a short email to the subcontractor about this line.",
    };

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isBusy;

    public string ContextText => _selection.SelectedLine is { } r
        ? $"CONTEXT: {r.Key}  |  {r.Status}"
        : $"CONTEXT: {_filter.Description}  |  {_selection.Screen.ToUpperInvariant()}";

    public string ModeText => AnthropicClient.ResolveKey(_data.Project.Settings.AnthropicApiKey) is null
        ? "OFFLINE - add an API key in Settings" : $"{_data.Project.Settings.AnthropicModel}  |  effort {_data.Project.Settings.AnthropicEffort}";

    public void Open(string? question = null)
    {
        IsOpen = true;
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ContextText));
        if (!string.IsNullOrWhiteSpace(question)) { Input = question; _ = Send(); }
    }

    [RelayCommand] private void Close() { IsOpen = false; _cts?.Cancel(); }
    [RelayCommand] private void Clear() { _cts?.Cancel(); Messages.Clear(); }
    [RelayCommand] private Task UseSuggestion(string? s) { Input = s ?? ""; return Send(); }

    [RelayCommand]
    private async Task Send()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsBusy) return;
        Input = "";
        Messages.Add(new AskMessage { Role = "user", Text = text });
        var answer = new AskMessage { Role = "assistant", Text = "" };
        Messages.Add(answer);
        IsBusy = true;
        var p = _data.Project;
        var selected = _selection.SelectedLine;
        var scope = _filter.Spec.Apply(p.Chain).ToList();
        var key = AnthropicClient.ResolveKey(p.Settings.AnthropicApiKey);
        try
        {
            if (key is null)
            {
                answer.Text = AskContextBuilder.OfflineAnswer(selected, p.Queue);
                return;
            }
            var system = AskContextBuilder.Build(p.Settings.EffectiveUserName, _filter.Spec, selected, scope, p.Queue, _selection.Screen);
            var client = new AnthropicClient(key)
            {
                Model = string.IsNullOrWhiteSpace(p.Settings.AnthropicModel) ? AnthropicClient.DefaultModel : p.Settings.AnthropicModel,
                Effort = p.Settings.AnthropicEffort,
                UseServerFallbacks = p.Settings.UseServerFallbacks,
            };
            var history = Messages.Where(m => m != answer && m.Text.Length > 0).Select(m => new ChatMessage(m.Role, m.Text)).ToList();
            _cts = new CancellationTokenSource();
            await foreach (var chunk in client.StreamAsync(system, history, _cts.Token))
                answer.Text += chunk;
            if (answer.Text.Length == 0) answer.Text = "(no answer)";
        }
        catch (OperationCanceledException) { answer.Text += " [stopped]"; }
        catch (AnthropicException ex) { answer.Text = ex.Message + "\n\n" + AskContextBuilder.OfflineAnswer(selected, p.Queue); }
        catch (HttpRequestException ex) { answer.Text = "Could not reach api.anthropic.com: " + ex.Message + "\n\n" + AskContextBuilder.OfflineAnswer(selected, p.Queue); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();
}
