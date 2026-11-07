using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuerySpace;

public sealed class CloudAgentService
{
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AiSettingsStore _settingsStore;

    public CloudAgentService(AiSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<AgentRunResult> RunAsync(
        CloudModelProfile profile,
        string prompt,
        AgentToolService tools,
        IProgress<string>? activity,
        IProgress<string>? streamedText,
        CancellationToken token)
    {
        var secret = _settingsStore.GetSecret(profile.Id);
        if (string.IsNullOrWhiteSpace(secret) && !IsOpenAiCompatible(profile))
            throw new InvalidOperationException($"No API key is stored for '{profile.Name}'. Open AI Settings and save its key.");
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException($"No model or Azure deployment is configured for '{profile.Name}'.");

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = AgentInstructions },
            new JsonObject { ["role"] = "user", ["content"] = prompt }
        };
        var toolCallCount = 0;
        var editorChanged = false;
        var failedCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        string? lastFailure = null;

        for (var step = 0; step < MaxAgentSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            activity?.Report(step == 0
                ? $"{profile.Name} is interpreting your request..."
                : $"The model is deciding the next action (step {step + 1} of {MaxAgentSteps})...");
            var body = new JsonObject
            {
                ["model"] = profile.Model,
                ["messages"] = messages.DeepClone(),
                ["temperature"] = 0.15,
                ["max_tokens"] = 2048,
                ["stream"] = true
            };
            if (profile.SupportsTools)
            {
                body["tools"] = BuildTools(tools.Definitions);
                body["tool_choice"] = "auto";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(profile));
            request.Content = JsonContent.Create(body);
            ApplyAuthentication(request, profile, secret);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(token);
                throw new InvalidOperationException($"{profile.Name} returned {(int)response.StatusCode}: {SafeApiError(responseText)}");
            }

            var streamed = await ReadStreamingResponseAsync(response, null, token);
            var calls = streamed.ToolCalls;
            if (calls.Count == 0 && TextToolCallParser.TryParse(streamed.Content, tools.Definitions, out var translatedCalls))
            {
                calls = translatedCalls;
                activity?.Report(AgentToolService.ActivityMessage(calls[0].Name));
            }
            messages.Add(streamed.AssistantMessage);
            if (calls.Count == 0)
            {
                if (LooksLikeRawToolProtocol(streamed.Content))
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "Do not echo raw tool responses, JSON, or tool protocol tags. Use the tool result to continue the task. For an editor request, call the required editor tool with the completed SQL."
                    });
                    activity?.Report($"The model echoed a tool response; retrying ({Math.Min(step + 2, MaxAgentSteps)} of {MaxAgentSteps})...");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(streamed.Content)) streamedText?.Report(streamed.Content);
                return new AgentRunResult
                {
                    Text = string.IsNullOrWhiteSpace(streamed.Content) ? "The cloud model completed without returning text." : streamed.Content.Trim(),
                    ToolCalls = toolCallCount,
                    EditorChanged = editorChanged
                };
            }

            var toolFailedThisStep = false;
            foreach (var call in calls)
            {
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
                    result = JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = ex.Message,
                        retryable = true,
                        instruction = "Use this tool error to decide the next action."
                    });
                    failedCalls.TryGetValue(callKey, out var failures);
                    failedCalls[callKey] = ++failures;
                    activity?.Report($"{call.Name} failed; asking the model to correct it...");
                    if (failures >= MaxIdenticalFailures)
                        throw new InvalidOperationException($"The cloud model repeated the same invalid {call.Name} request {failures} times. Last error: {ex.Message}", ex);
                }
                if (IsEditorTool(call.Name) && result.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase))
                {
                    editorChanged = true;
                }
                if (TextToolCallParser.IsTranslated(call))
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = $"QuerySpace executed the tool call you requested. Tool: {call.Name}. TOOL_RESULT: {result}\nUse this result to decide the next action."
                    });
                }
                else
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = result
                    });
                }
            }
            activity?.Report(toolFailedThisStep
                ? "The tool reported an error. The model is reviewing it..."
                : "The tool completed. The model is reviewing the result...");
        }

        throw new InvalidOperationException($"The cloud agent reached the safety limit of {MaxAgentSteps} model/tool steps. Last tool error: {lastFailure ?? "none"}.");
    }

    private const int MaxAgentSteps = 16;
    private const int MaxIdenticalFailures = 4;

    public async Task TestAsync(CloudModelProfile profile, string? secret, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new InvalidOperationException("Enter the model name used by the custom provider.");
        var body = new JsonObject
        {
            ["model"] = profile.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with OK." }),
            ["temperature"] = 0,
            ["max_tokens"] = 8
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(profile));
        request.Content = JsonContent.Create(body);
        ApplyAuthentication(request, profile, secret);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Connection test returned {(int)response.StatusCode}: {SafeApiError(text)}");
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new InvalidOperationException("The endpoint returned HTTP 200 but not an OpenAI-compatible chat-completions response (the 'choices' array is missing). Check the endpoint and model name.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The endpoint returned HTTP 200 but the response was not valid JSON. Check that the endpoint is an OpenAI-compatible /chat/completions endpoint.", ex);
        }
    }

    private static JsonArray BuildTools(IEnumerable<AgentToolDefinition> definitions)
    {
        var result = new JsonArray();
        foreach (var tool in definitions)
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.JsonSchema)
                }
            });
        }
        return result;
    }

    private static List<AgentToolCall> ParseToolCalls(JsonElement message)
    {
        var calls = new List<AgentToolCall>();
        if (!message.TryGetProperty("tool_calls", out var elements) || elements.ValueKind != JsonValueKind.Array) return calls;
        foreach (var element in elements.EnumerateArray())
        {
            var function = element.GetProperty("function");
            calls.Add(new AgentToolCall(
                element.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                function.GetProperty("name").GetString() ?? "",
                function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}"));
        }
        return calls;
    }

    private static async Task<CloudStreamResult> ReadStreamingResponseAsync(
        HttpResponseMessage response,
        IProgress<string>? streamedText,
        CancellationToken token)
    {
        var payload = await response.Content.ReadAsStringAsync(token);
        if (!payload.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ReadJsonResponse(payload, streamedText);

        using var reader = new StringReader(payload);
        var content = new StringBuilder();
        var builders = new Dictionary<int, StreamingToolCall>();

        while (reader.Peek() >= 0)
        {
            token.ThrowIfCancellationRequested();
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
            {
                var value = contentElement.GetString() ?? "";
                if (value.Length > 0)
                {
                    content.Append(value);
                    streamedText?.Report(value);
                }
            }
            if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array) continue;
            foreach (var element in toolCalls.EnumerateArray())
            {
                var index = element.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : builders.Count;
                if (!builders.TryGetValue(index, out var builder)) builders[index] = builder = new StreamingToolCall();
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) builder.Id = id.GetString() ?? builder.Id;
                if (!element.TryGetProperty("function", out var function)) continue;
                if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) builder.Name.Append(name.GetString());
                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String) builder.Arguments.Append(arguments.GetString());
            }
        }

        var calls = builders.OrderBy(pair => pair.Key).Select(pair => new AgentToolCall(
            string.IsNullOrWhiteSpace(pair.Value.Id) ? $"call_{pair.Key}" : pair.Value.Id,
            pair.Value.Name.ToString(),
            pair.Value.Arguments.Length == 0 ? "{}" : pair.Value.Arguments.ToString())).ToList();
        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content.Length == 0 ? null : content.ToString()
        };
        if (calls.Count > 0)
        {
            var callArray = new JsonArray();
            foreach (var call in calls)
            {
                callArray.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
                });
            }
            assistant["tool_calls"] = callArray;
        }
        return new CloudStreamResult(content.ToString(), calls, assistant);
    }

    private static CloudStreamResult ReadJsonResponse(string payload, IProgress<string>? streamedText)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new InvalidOperationException("The custom provider response does not contain an OpenAI-compatible 'choices' array.");

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The custom provider response does not contain choices[0].message.");

            var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? ""
                : "";
            if (content.Length > 0) streamedText?.Report(content);
            var calls = ParseToolCalls(message);
            var assistant = JsonNode.Parse(message.GetRawText())?.AsObject() ?? new JsonObject();
            assistant["role"] = "assistant";
            return new CloudStreamResult(content, calls, assistant);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The custom provider returned neither an SSE stream nor a valid OpenAI-compatible JSON response.", ex);
        }
    }

    private static bool IsEditorTool(string name) =>
        name.Equals("insert_sql_in_editor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("replace_active_query", StringComparison.OrdinalIgnoreCase);


    private static bool LooksLikeRawToolProtocol(string text) =>
        text.Contains("<tool_response>", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("</tool_response>", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

    private static Uri BuildEndpoint(CloudModelProfile profile)
    {
        var endpoint = profile.Endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _)) throw new InvalidOperationException("Enter an absolute HTTPS model endpoint.");
        if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cloud endpoints must use HTTPS. Plain HTTP is allowed only for localhost.");

        if (endpoint.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)) return new Uri(endpoint);
        if (profile.Provider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var apiVersion = Uri.EscapeDataString(string.IsNullOrWhiteSpace(profile.ApiVersion) ? "2024-10-21" : profile.ApiVersion);
            return new Uri($"{endpoint}/openai/deployments/{Uri.EscapeDataString(profile.Model)}/chat/completions?api-version={apiVersion}");
        }
        return new Uri(endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? endpoint + "/chat/completions"
            : endpoint + "/v1/chat/completions");
    }

    private static bool IsOpenAiCompatible(CloudModelProfile profile) =>
        profile.Provider.Equals("OpenAI Compatible", StringComparison.OrdinalIgnoreCase);

    private static void ApplyAuthentication(HttpRequestMessage request, CloudModelProfile profile, string? secret)
    {
        if (profile.Provider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("An API key is required for Azure OpenAI.");
            request.Headers.Add("api-key", secret);
            return;
        }

        // OpenAI-compatible localhost/custom servers commonly use no authentication. When a key is
        // supplied, send the standard Bearer header expected by OpenAI and most compatible APIs.
        if (!string.IsNullOrWhiteSpace(secret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    private static string SafeApiError(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return "No response body.";
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)) return message.GetString() ?? "Unknown API error.";
                return error.ToString();
            }
        }
        catch { }
        return response.Length > 1_000 ? response[..1_000] : response;
    }

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

    private sealed record CloudStreamResult(string Content, List<AgentToolCall> ToolCalls, JsonObject AssistantMessage);

    private sealed class StreamingToolCall
    {
        public string Id { get; set; } = "";
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}
