using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace QuerySpace;

public partial class AiAssistantPanel : System.Windows.Controls.UserControl
{
    private readonly AiSettingsStore _settingsStore;
    private readonly FoundryLocalService _localModels;
    private readonly AiService _aiService;
    private readonly AgentToolService _tools;
    private readonly ObservableCollection<ChatDisplayItem> _conversation = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _warmupCts;

    public AiAssistantPanel(
        AiSettingsStore settingsStore,
        FoundryLocalService localModels,
        AiService aiService,
        AgentToolService tools)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _localModels = localModels;
        _aiService = aiService;
        _tools = tools;
        ConversationList.ItemsSource = _conversation;
        Loaded += async (_, _) => await RefreshModelsAsync();
        Unloaded += (_, _) => { _runCts?.Cancel(); _warmupCts?.Cancel(); };
    }

    public event EventHandler? CloseRequested;
    public event Action<string>? ModelChanged;

    public void RefreshModels() => _ = RefreshModelsAsync();

    public void Stop() => _runCts?.Cancel();

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        var selectedKey = (ModelCombo.SelectedItem as AiModelOption)?.Key ?? _settingsStore.Load().DefaultModelKey;
        AgentStatus.Text = "Loading configured models...";
        try
        {
            var models = await _aiService.GetAvailableModelsAsync(CancellationToken.None);
            ModelCombo.ItemsSource = models;
            AgentStatus.Text = models.Count == 0 ? "No models are installed or configured." : $"{models.Count} model(s) available";
            ModelCombo.SelectedItem = models.FirstOrDefault(m => m.Key == selectedKey) ?? models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AgentStatus.Text = "Models could not be loaded.";
            MessageBox.Show(Window.GetWindow(this), ex.Message, "QuerySpace Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not AiModelOption model) return;
        var settings = _settingsStore.Load();
        settings.DefaultModelKey = model.Key;
        _settingsStore.Save(settings);
        HintText.Text = $"{model.Provider} • {model.LocationLabel} • {model.CapabilityLabel}. Agent access follows MCP and dataset permissions.";
        ModelChanged?.Invoke($"AI: {model.DisplayName}");
        await WarmSelectedModelAsync(model);
    }

    private async Task WarmSelectedModelAsync(AiModelOption model)
    {
        _warmupCts?.Cancel();
        _warmupCts?.Dispose();
        _warmupCts = null;
        if (!model.IsLocal)
        {
            AgentProgress.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = _runCts == null;
            AgentStatus.Text = "Cloud model ready";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _warmupCts = cancellation;
        SendButton.IsEnabled = false;
        AgentProgress.Visibility = Visibility.Visible;
        AgentStatus.Text = $"Loading {model.DisplayName} into memory...";
        var progress = new Progress<string>(message => AgentStatus.Text = message);
        try
        {
            var status = await _localModels.PreloadAsync(model.ModelId, progress, cancellation.Token);
            if (!cancellation.IsCancellationRequested && (ModelCombo.SelectedItem as AiModelOption)?.Key == model.Key)
                AgentStatus.Text = status;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) AgentStatus.Text = $"Model load failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_warmupCts, cancellation))
            {
                _warmupCts.Dispose();
                _warmupCts = null;
                AgentProgress.Visibility = Visibility.Collapsed;
                SendButton.IsEnabled = _runCts == null;
            }
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_runCts != null) return;
        if (_warmupCts != null)
        {
            AgentStatus.Text = "The local model is still loading. Send will be available when it is ready.";
            return;
        }
        if (ModelCombo.SelectedItem is not AiModelOption model)
        {
            MessageBox.Show(Window.GetWindow(this), "Download a local model or configure a cloud model in AI Settings first.", "QuerySpace Assistant");
            return;
        }
        var prompt = PromptText.Text.Trim();
        if (prompt.Length == 0) return;
        if (!_settingsStore.Load().McpEnabled)
        {
            MessageBox.Show(Window.GetWindow(this), "Enable QuerySpace MCP and AI tools in AI Settings before running an agent task.", "QuerySpace Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _conversation.Add(new ChatDisplayItem("You", prompt));
        var assistantItem = new ChatDisplayItem(model.DisplayName, "");
        _conversation.Add(assistantItem);
        ConversationList.ScrollIntoView(assistantItem);
        PromptText.Clear();
        SetBusy(true, "Starting agent...");
        _runCts = new CancellationTokenSource();
        var finalResponseStarted = false;
        var progress = new Progress<string>(message =>
        {
            AgentStatus.Text = message;
            if (!finalResponseStarted)
            {
                assistantItem.Text = message;
                ConversationList.ScrollIntoView(assistantItem);
            }
        });
        var streamedText = new Progress<string>(delta =>
        {
            if (!finalResponseStarted)
            {
                finalResponseStarted = true;
                assistantItem.Text = "";
            }
            assistantItem.Append(delta);
            ConversationList.ScrollIntoView(assistantItem);
        });
        try
        {
            var result = await _aiService.RunAsync(model, prompt, _tools, progress, streamedText, _runCts.Token);
            assistantItem.Text = result.Text;
            AgentStatus.Text = result.EditorChanged
                ? $"Query editor updated using {result.ToolCalls} tool call(s)"
                : result.ToolCalls == 0 ? "Completed" : $"Completed using {result.ToolCalls} tool call(s)";
            ConversationList.ScrollIntoView(assistantItem);
        }
        catch (OperationCanceledException)
        {
            assistantItem.Append(string.IsNullOrWhiteSpace(assistantItem.Text) ? "The agent task was stopped." : "\n\n[Stopped]");
            AgentStatus.Text = "Stopped";
        }
        catch (Exception ex)
        {
            assistantItem.Role = "QuerySpace error";
            assistantItem.Text = ex.Message;
            AgentStatus.Text = "Agent failed";
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetBusy(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AgentStatus.Text = "Stopping...";
        _runCts?.Cancel();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsDialog(_settingsStore, _localModels) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        await RefreshModelsAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async void PromptText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        SendButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        ModelCombo.IsEnabled = !busy;
        AgentProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status != null) AgentStatus.Text = status;
    }

    private sealed class ChatDisplayItem : INotifyPropertyChanged
    {
        private string _role;
        private string _text;

        public ChatDisplayItem(string role, string text)
        {
            _role = role;
            _text = text;
        }

        public string Role
        {
            get => _role;
            set { if (_role == value) return; _role = value; OnPropertyChanged(); }
        }

        public string Text
        {
            get => _text;
            set { if (_text == value) return; _text = value; OnPropertyChanged(); }
        }

        public void Append(string value)
        {
            if (!string.IsNullOrEmpty(value)) Text += value;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
