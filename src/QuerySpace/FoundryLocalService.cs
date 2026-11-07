using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QuerySpace;

public sealed class FoundryLocalService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private IModel? _loadedModel;

    public async Task<string> GetExecutionProviderStatusAsync(CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var providers = FoundryLocalManager.Instance.DiscoverEps();
        if (providers.Length == 0)
            return "Foundry Local reported no optional hardware execution providers; CPU/WebGPU fallback remains available.";

        return "Hardware providers: " + string.Join(", ", providers.Select(provider =>
            $"{provider.Name} ({(provider.IsRegistered ? "ready" : "not installed")})"));
    }

    public async Task<string> EnableGpuAccelerationAsync(
        IProgress<(string Provider, double Percent)>? progress,
        CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var manager = FoundryLocalManager.Instance;
        var providers = manager.DiscoverEps();
        if (providers.Length == 0)
            return "No compatible optional GPU execution provider was discovered. Foundry Local will retain its built-in CPU/WebGPU fallback.";

        var pending = providers.Where(provider => !provider.IsRegistered).Select(provider => provider.Name).ToArray();
        if (pending.Length > 0)
        {
            var result = await manager.DownloadAndRegisterEpsAsync(
                pending,
                (name, value) => progress?.Report((name, value)),
                token);
            if (!result.Success)
                throw new InvalidOperationException($"GPU provider setup failed: {result.Status}");
        }

        var ready = manager.DiscoverEps().Where(provider => provider.IsRegistered).Select(provider => provider.Name).ToArray();
        return ready.Length == 0
            ? "No optional GPU provider was registered. Foundry Local will use its built-in CPU/WebGPU fallback."
            : "GPU acceleration ready: " + string.Join(", ", ready);
    }

    public async Task<IReadOnlyList<LocalModelInfo>> GetModelsAsync(bool cachedOnly, CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(token);
        var models = cachedOnly
            ? await catalog.GetCachedModelsAsync(token)
            : await catalog.ListModelsAsync(token);

        var result = new List<LocalModelInfo>();
        foreach (var group in models
                     .Where(m => m.Info.Task?.Contains("chat", StringComparison.OrdinalIgnoreCase) == true)
                     .GroupBy(m => m.Alias, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.First().Info.DisplayName))
        {
            token.ThrowIfCancellationRequested();
            var model = await catalog.GetModelAsync(group.Key, token) ?? group.First();
            var cached = model.Info.Cached || await model.IsCachedAsync(token);
            result.Add(ToInfo(model, cached));
        }
        return result;
    }

    public async Task RedetectHardwareAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_loadedModel != null)
            {
                await _loadedModel.UnloadAsync(token);
                _loadedModel = null;
            }
            if (_initialized)
            {
                FoundryLocalManager.Instance.Shutdown();
                _initialized = false;
            }
        }
        finally { _gate.Release(); }
        await EnsureInitializedAsync(token);
    }

    public async Task DownloadAsync(string alias, IProgress<double>? progress, CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var model = await GetModelAsync(alias, token);
        await model.DownloadAsync(value => progress?.Report(value), token);
    }

    public async Task RemoveAsync(string alias, CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var model = await GetModelAsync(alias, token);
        if (_loadedModel?.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) == true)
        {
            await _loadedModel.UnloadAsync(token);
            _loadedModel = null;
        }
        await model.RemoveFromCacheAsync(token);
    }

    public async Task<string> PreloadAsync(string alias, IProgress<string>? activity, CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var model = await LoadModelAsync(alias, activity, token);
        var device = model.Info.Runtime?.DeviceType.ToString() ?? "available hardware";
        var provider = model.Info.Runtime?.ExecutionProvider;
        return string.IsNullOrWhiteSpace(provider)
            ? $"{model.Info.DisplayName} ready on {device}"
            : $"{model.Info.DisplayName} ready on {device} / {provider}";
    }

    public async Task<AgentRunResult> RunAgentAsync(
        string alias,
        string prompt,
        AgentToolService tools,
        IProgress<string>? activity,
        IProgress<string>? streamedText,
        CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var model = await LoadModelAsync(alias, activity, token);
        using var session = new ChatSession(model);
        session.SetStreaming(true);
        session.SetOptions(new RequestOptions
        {
            ToolChoice = ToolChoice.Auto,
            Search = new SearchOptions { Temperature = 0.15f, MaxOutputTokens = 2048 }
        });

        foreach (var tool in tools.Definitions)
        {
            session.AddToolDefinition(tool.Name, tool.Description, tool.JsonSchema);
        }

        using var firstRequest = new Request();
        firstRequest.AddItem(MessageItem.System(AgentInstructions, "QuerySpace"));
        firstRequest.AddItem(MessageItem.User(prompt, "user"));
        return await RunToolLoopAsync(session, firstRequest, tools, activity, streamedText, token);
    }

    public IChatClient CreateChatClient(string alias, IProgress<string>? activity) =>
        new QuerySpaceChatClient("foundry-local", alias,
            (messages, options, token) => CompleteChatAsync(alias, messages, options, activity, token));

    private async Task<ChatResponse> CompleteChatAsync(
        string alias,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IProgress<string>? activity,
        CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        var model = await LoadModelAsync(alias, activity, token);
        var history = messages.ToList();
        var latestUserIndex = history.FindLastIndex(message =>
            message.Role == ChatRole.User && message.Contents.OfType<TextContent>().Any(content => !string.IsNullOrWhiteSpace(content.Text)));
        var currentTurn = history.Skip(Math.Max(0, latestUserIndex + 1)).ToList();
        var completionCallIds = currentTurn.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(call => call.Name.Equals("complete_task", StringComparison.OrdinalIgnoreCase))
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);
        var taskCompleted = currentTurn.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Any(result => completionCallIds.Contains(result.CallId) &&
                           Convert.ToString(result.Result)?.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase) == true);
        using var session = new ChatSession(model);
        session.SetStreaming(true);
        session.SetOptions(new RequestOptions
        {
            // A small local model may ignore optional tools and answer with invented prose. Requiring a
            // model-selected tool preserves model agency while making the agent protocol reliable.
            ToolChoice = taskCompleted ? ToolChoice.Auto : ToolChoice.Required,
            Search = new SearchOptions { Temperature = 0.15f, MaxOutputTokens = options?.MaxOutputTokens ?? 2048 }
        });

        var definitions = new List<AgentToolDefinition>();
        foreach (var function in options?.Tools?.OfType<AIFunctionDeclaration>() ?? [])
        {
            var schema = function.JsonSchema.ValueKind == JsonValueKind.Undefined ? "{}" : function.JsonSchema.GetRawText();
            session.AddToolDefinition(function.Name, function.Description ?? "", schema);
            definitions.Add(new AgentToolDefinition(function.Name, function.Description ?? "", schema));
        }
        if (definitions.Count == 0)
            throw new InvalidOperationException("Microsoft Agent Framework did not provide any QuerySpace tools to the local model.");
        activity?.Report($"The local model received {definitions.Count} QuerySpace tools and the full conversation context...");

        using var request = new Request();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            request.AddItem(MessageItem.System(options.Instructions, "QuerySpace"));
        foreach (var message in history)
        {
            var text = FlattenMessage(message);
            if (text.Length == 0) continue;
            if (message.Role == ChatRole.System) request.AddItem(MessageItem.System(text, "QuerySpace"));
            else if (message.Role == ChatRole.Assistant) request.AddItem(MessageItem.Assistant(text, "assistant"));
            else request.AddItem(MessageItem.User(text, "user"));
        }

        await using var stream = session.ProcessStreamingRequestAsync(request, token);
        var textResult = new StringBuilder();
        var calls = new List<AgentToolCall>();
        await foreach (var item in stream.WithCancellation(token)) ReadResponseItem(item, textResult, calls);
        using var response = await stream.FinalResponse;
        var finalText = new StringBuilder();
        var finalCalls = new List<AgentToolCall>();
        foreach (var item in response) ReadResponseItem(item, finalText, finalCalls);
        if (textResult.Length == 0) textResult.Append(finalText);
        if (finalCalls.Count > 0) calls = finalCalls;
        if (calls.Count == 0 && TextToolCallParser.TryParse(textResult.ToString(), definitions, out var translated)) calls = translated;

        var contents = new List<AIContent>();
        if (textResult.Length > 0 && calls.Count == 0) contents.Add(new TextContent(textResult.ToString()));
        foreach (var call in calls)
        {
            Dictionary<string, object?> arguments;
            try { arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments) ?? new(); }
            catch { arguments = new(); }
            contents.Add(new FunctionCallContent(call.Id, call.Name, arguments!));
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
    }

    private static string FlattenMessage(ChatMessage message)
    {
        var parts = new List<string>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(text.Text);
                    break;
                case FunctionCallContent call:
                    parts.Add($"The assistant selected QuerySpace tool '{call.Name}' with arguments {JsonSerializer.Serialize(call.Arguments)}.");
                    break;
                case FunctionResultContent result:
                    parts.Add($"QuerySpace returned this result for tool call {result.CallId}: {Convert.ToString(result.Result)}");
                    break;
            }
        }
        return string.Join("\n", parts);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loadedModel != null)
            {
                try { await _loadedModel.UnloadAsync(CancellationToken.None); } catch { }
                _loadedModel = null;
            }
            if (_initialized)
            {
                FoundryLocalManager.Instance.Shutdown();
                _initialized = false;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<AgentRunResult> RunToolLoopAsync(
        ChatSession session,
        Request initialRequest,
        AgentToolService tools,
        IProgress<string>? activity,
        IProgress<string>? streamedText,
        CancellationToken token)
    {
        Request? currentRequest = initialRequest;
        var toolCallCount = 0;
        var editorChanged = false;
        var failedCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        string? lastFailure = null;
        for (var step = 0; step < MaxAgentSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            activity?.Report(step == 0
                ? "The model is interpreting your request..."
                : $"The model is deciding the next action (step {step + 1} of {MaxAgentSteps})...");
            session.SetOptions(new RequestOptions
            {
                ToolChoice = ToolChoice.Auto,
                Search = new SearchOptions { Temperature = 0.15f, MaxOutputTokens = 2048 }
            });

            await using var stream = session.ProcessStreamingRequestAsync(currentRequest, token);
            var text = new StringBuilder();
            var calls = new List<AgentToolCall>();
            await foreach (var item in stream.WithCancellation(token))
            {
                ReadResponseItem(item, text, calls);
            }
            using var response = await stream.FinalResponse;
            if (!ReferenceEquals(currentRequest, initialRequest)) currentRequest.Dispose();
            currentRequest = null;

            var finalText = new StringBuilder();
            var finalCalls = new List<AgentToolCall>();
            foreach (var item in response)
            {
                ReadResponseItem(item, finalText, finalCalls);
            }
            if (text.Length == 0 && finalText.Length > 0)
            {
                text.Append(finalText);
            }
            if (finalCalls.Count > 0) calls = finalCalls;
            if (calls.Count == 0 && TextToolCallParser.TryParse(text.ToString(), tools.Definitions, out var translatedCalls))
            {
                calls = translatedCalls;
                activity?.Report(AgentToolService.ActivityMessage(calls[0].Name));
            }

            if (calls.Count == 0)
            {
                if (LooksLikeRawToolProtocol(text.ToString()))
                {
                    currentRequest = new Request();
                    currentRequest.AddItem(MessageItem.User(
                        "Do not echo raw tool responses, JSON, or tool protocol tags. Use the tool result to continue the task. For an editor request, call the required editor tool with the completed SQL.",
                        "user"));
                    activity?.Report($"The model echoed a tool response; retrying ({Math.Min(step + 2, MaxAgentSteps)} of {MaxAgentSteps})...");
                    continue;
                }
                if (text.Length > 0) streamedText?.Report(text.ToString());
                return new AgentRunResult
                {
                    Text = text.Length == 0 ? "The local model completed without returning text." : text.ToString().Trim(),
                    ToolCalls = toolCallCount,
                    EditorChanged = editorChanged
                };
            }

            currentRequest = new Request();
            var toolFailedThisStep = false;
            foreach (var call in calls)
            {
                token.ThrowIfCancellationRequested();
                toolCallCount++;
                activity?.Report(AgentToolService.ActivityMessage(call.Name));
                string result;
                var callKey = call.Name + "\n" + call.Arguments;
                try
                {
                    result = await tools.ExecuteAsync(call, token);
                }
                catch (Exception ex)
                {
                    toolFailedThisStep = true;
                    lastFailure = ex.Message;
                    result = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = ex.Message,
                        retryable = true,
                        instruction = "Use this tool error to decide the next action."
                    });
                    failedCalls.TryGetValue(callKey, out var failures);
                    failedCalls[callKey] = ++failures;
                    activity?.Report($"{call.Name} failed; returning the error to the model for correction...");
                    if (failures >= MaxIdenticalFailures)
                        throw new InvalidOperationException($"The local model repeated the same invalid {call.Name} request {failures} times. Last error: {ex.Message}", ex);
                }
                if (IsEditorTool(call.Name) && result.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase))
                {
                    editorChanged = true;
                }
                if (TextToolCallParser.IsTranslated(call))
                {
                    currentRequest.AddItem(MessageItem.User(
                        $"QuerySpace executed the tool call you requested. Tool: {call.Name}. TOOL_RESULT: {result}\nUse this result to decide the next action.",
                        "user"));
                }
                else
                {
                    currentRequest.AddItem(new ToolResultItem(call.Id, result));
                }
            }
            activity?.Report(toolFailedThisStep
                ? "The tool reported an error. The model is reviewing it..."
                : "The tool completed. The model is reviewing the result...");
        }

        currentRequest?.Dispose();
        throw new InvalidOperationException($"The local agent reached the safety limit of {MaxAgentSteps} model/tool steps. Last tool error: {lastFailure ?? "none"}.");
    }

    private const int MaxAgentSteps = 16;
    private const int MaxIdenticalFailures = 4;

    private static bool IsEditorTool(string name) =>
        name.Equals("insert_sql_in_editor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("replace_active_query", StringComparison.OrdinalIgnoreCase);


    private static bool LooksLikeRawToolProtocol(string text) =>
        text.Contains("<tool_response>", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("</tool_response>", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

    private static void ReadResponseItem(Item item, StringBuilder text, List<AgentToolCall> calls)
    {
        switch (item)
        {
            case TextItem textItem when !string.IsNullOrWhiteSpace(textItem.Text):
                text.Append(textItem.Text);
                break;
            case ToolCallItem toolCall:
                calls.Add(new AgentToolCall(toolCall.CallId, toolCall.Name, toolCall.Arguments));
                break;
            case MessageItem message:
                if (message.IsSimpleText())
                {
                    text.Append(message.GetSimpleText());
                    break;
                }
                foreach (var part in message.Parts) ReadResponseItem(part, text, calls);
                break;
        }
    }

    private async Task<IModel> LoadModelAsync(string alias, IProgress<string>? activity, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_loadedModel?.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) == true &&
                await _loadedModel.IsLoadedAsync(token))
            {
                return _loadedModel;
            }

            if (_loadedModel != null)
            {
                activity?.Report($"Unloading {_loadedModel.Alias}");
                await _loadedModel.UnloadAsync(token);
            }

            var model = await GetModelAsync(alias, token);
            if (!await model.IsCachedAsync(token))
            {
                throw new InvalidOperationException($"Local model '{alias}' is not downloaded. Open AI Settings and download it first.");
            }
            activity?.Report($"Loading {model.Info.DisplayName}");
            await model.LoadAsync(token);
            _loadedModel = model;
            return model;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IModel> GetModelAsync(string alias, CancellationToken token)
    {
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(token);
        return await catalog.GetModelAsync(alias, token)
               ?? throw new InvalidOperationException($"Local model '{alias}' was not found in the Foundry Local catalog.");
    }

    private async Task EnsureInitializedAsync(CancellationToken token)
    {
        if (_initialized) return;
        await _gate.WaitAsync(token);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(AiSettingsStore.ModelsFolder);
            var appData = Path.Combine(AiSettingsStore.AppDataFolder, "foundry-local");
            await FoundryLocalManager.CreateAsync(new Configuration
            {
                AppName = "QuerySpace",
                AppDataDir = appData,
                ModelCacheDir = AiSettingsStore.ModelsFolder,
                LogsDir = Path.Combine(appData, "logs"),
                LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Warning,
                DisableNonessentialTelemetry = true
            }, NullLogger.Instance, token);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static LocalModelInfo ToInfo(IModel model, bool cached) => new()
    {
        Alias = model.Alias,
        DisplayName = string.IsNullOrWhiteSpace(model.Info.DisplayName) ? model.Alias : model.Info.DisplayName,
        Publisher = model.Info.Publisher ?? "Unknown publisher",
        License = model.Info.License ?? "See model documentation",
        LicenseDescription = model.Info.LicenseDescription ?? "",
        FileSizeMb = model.Info.FileSizeMb,
        ContextLength = model.Info.ContextLength,
        SupportsTools = model.Info.SupportsToolCalling == true,
        Device = model.Info.Runtime?.DeviceType.ToString() ?? "Unknown",
        ExecutionProvider = model.Info.Runtime?.ExecutionProvider ?? "Unknown",
        IsCached = cached
    };

    private const string AgentInstructions = """
        You are the QuerySpace tool-calling data assistant. Infer the user's intent from their complete request and choose whether
        and how to use the supplied tools. QuerySpace only executes the tool calls you make; it does not choose tools for you.
        Treat every explicit user instruction and constraint as authoritative and do not substitute a preferred approach.

        Use list_datasets, get_dataset_schema, sample_dataset, or get_active_query whenever you need information. After each tool call,
        read the returned result before deciding the next action. Never invent a tool result, dataset, column, or active query.
        Use insert_sql_in_editor or replace_active_query when the user's intended outcome is SQL in the editor. Use validate_sql or
        run_readonly_sql when validation or execution is part of the intended task. QuerySpace uses DuckDB SQL over local Parquet data.

        A tool error is information for your next decision. Revise the arguments, obtain missing information with another tool, ask
        the user for genuinely unavailable information, or explain the failure. Never claim that an action completed unless its tool
        returned success. After the task is complete, give a concise final response describing the actual result.
        """;
}
