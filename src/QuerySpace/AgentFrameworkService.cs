using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace QuerySpace;

public sealed class AgentFrameworkService
{
    private readonly ConcurrentDictionary<string, AgentConversation> _conversations = new(StringComparer.Ordinal);

    public async Task<AgentRunResult> RunAsync(
        string conversationKey,
        Func<IChatClient> createClient,
        string prompt,
        AgentToolService tools,
        IProgress<string>? activity,
        IProgress<string>? streamedText,
        CancellationToken token)
    {
        var conversation = _conversations.GetOrAdd(conversationKey, _ => CreateConversation(createClient(), tools));
        await conversation.Gate.WaitAsync(token);
        try
        {
            tools.BeginAgentRun();
            conversation.ResetRunState(activity);
            activity?.Report("Microsoft Agent Framework is interpreting your complete request...");

            var response = await conversation.Agent.RunAsync(prompt, conversation.Session, cancellationToken: token);
            var text = response.Text?.Trim() ?? "";
            if (text.Length > 0) streamedText?.Report(text);
            return new AgentRunResult
            {
                Text = text.Length == 0 ? "The model completed without returning text." : text,
                ToolCalls = conversation.ToolCalls,
                EditorChanged = conversation.EditorChanged
            };
        }
        finally
        {
            conversation.Gate.Release();
        }
    }

    public void Clear() => _conversations.Clear();

    private static AgentConversation CreateConversation(IChatClient client, AgentToolService tools)
    {
        var state = new AgentConversation();
        var definitions = tools.Definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);

        Task<string> InvokeAsync(string name, object arguments, CancellationToken token)
        {
            Interlocked.Increment(ref state.ToolCalls);
            state.Activity?.Report(AgentToolService.ActivityMessage(name));
            var json = JsonSerializer.Serialize(arguments);
            return InvokeCoreAsync(name, json, token);
        }

        Task<string> CompleteTaskAsync(string summary, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref state.ToolCalls);
            if (state.EditorChanged && !state.CompletionReviewRequested)
            {
                state.CompletionReviewRequested = true;
                state.Activity?.Report("The model must review the SQL it inserted before completion...");
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = false,
                    reviewRequired = true,
                    instruction = "Re-read the latest user request and every constraint. Call get_active_query, compare the actual SQL against that request, and correct it with replace_active_query if it does not fully perform the requested task. Then call complete_task again."
                }));
            }
            if (state.EditorChanged && !state.ActiveQueryReviewed)
            {
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = false,
                    reviewRequired = true,
                    instruction = "Call get_active_query and review the actual SQL against the latest user request before completing."
                }));
            }
            state.Activity?.Report("The model marked the requested task complete...");
            return Task.FromResult(JsonSerializer.Serialize(new { success = true, summary }));
        }

        async Task<string> InvokeCoreAsync(string name, string json, CancellationToken token)
        {
            var result = await tools.ExecuteAsync(new AgentToolCall(Guid.NewGuid().ToString("N"), name, json), token);
            if (definitions[name].ChangesState && result.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase))
                state.EditorChanged = true;
            if (state.CompletionReviewRequested && name.Equals("get_active_query", StringComparison.OrdinalIgnoreCase))
                state.ActiveQueryReviewed = true;
            state.Activity?.Report($"{name} completed. The model is reviewing the result...");
            return result;
        }

        var functions = new List<AITool>
        {
            AIFunctionFactory.Create((CancellationToken ct) => InvokeAsync("list_datasets", new { }, ct), "list_datasets", definitions["list_datasets"].Description),
            AIFunctionFactory.Create((string datasetName, CancellationToken ct) => InvokeAsync("get_dataset_schema", new { datasetName }, ct), "get_dataset_schema", definitions["get_dataset_schema"].Description),
            AIFunctionFactory.Create((string datasetName, int rows, CancellationToken ct) => InvokeAsync("sample_dataset", new { datasetName, rows }, ct), "sample_dataset", definitions["sample_dataset"].Description),
            AIFunctionFactory.Create((string sql, CancellationToken ct) => InvokeAsync("validate_sql", new { sql }, ct), "validate_sql", definitions["validate_sql"].Description),
            AIFunctionFactory.Create((string sql, int rows, CancellationToken ct) => InvokeAsync("run_readonly_sql", new { sql, rows }, ct), "run_readonly_sql", definitions["run_readonly_sql"].Description),
            AIFunctionFactory.Create((CancellationToken ct) => InvokeAsync("get_active_query", new { }, ct), "get_active_query", definitions["get_active_query"].Description),
            AIFunctionFactory.Create((string sql, CancellationToken ct) => InvokeAsync("insert_sql_in_editor", new { sql }, ct), "insert_sql_in_editor", definitions["insert_sql_in_editor"].Description),
            AIFunctionFactory.Create((string sql, CancellationToken ct) => InvokeAsync("replace_active_query", new { sql }, ct), "replace_active_query", definitions["replace_active_query"].Description),
            AIFunctionFactory.Create((string summary, CancellationToken ct) => CompleteTaskAsync(summary, ct), "complete_task",
                "Call only after the user's requested outcome has actually been completed. For SQL editor tasks, do not call this until insert_sql_in_editor or replace_active_query succeeds. The summary must describe the completed result.")
        };

        var agent = client.AsHarnessAgent(new HarnessAgentOptions
        {
            // The default harness prompt is designed for larger general-purpose models. Keep the
            // contract short and explicit for the embedded 7B coder models.
            HarnessInstructions = "",
            ChatOptions = new ChatOptions
            {
                Instructions = AgentInstructions,
                Tools = functions
            },
            MaximumIterationsPerRequest = 16,
            DisableWebSearch = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true
        }, NullLoggerFactory.Instance);

        state.Agent = agent;
        state.Session = agent.CreateSessionAsync().AsTask().GetAwaiter().GetResult();
        return state;
    }

    private sealed class AgentConversation
    {
        public AIAgent Agent { get; set; } = null!;
        public AgentSession Session { get; set; } = null!;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IProgress<string>? Activity { get; private set; }
        public int ToolCalls;
        public bool EditorChanged;
        public bool CompletionReviewRequested;
        public bool ActiveQueryReviewed;

        public void ResetRunState(IProgress<string>? activity)
        {
            Activity = activity;
            ToolCalls = 0;
            EditorChanged = false;
            CompletionReviewRequested = false;
            ActiveQueryReviewed = false;
        }
    }

    private const string AgentInstructions = """
        You are QuerySpace's tool-using SQL agent. Follow the user's complete request exactly. You choose the tools; QuerySpace only
        executes your tool calls. Do not invent dataset names, columns, schemas, query contents, or tool results.

        REQUIRED WORKFLOW FOR SQL ABOUT A QUERYSPACE DATASET:
        1. Call list_datasets to identify the exact dataset.
        2. Call get_dataset_schema with that exact name before writing SQL.
        3. Create DuckDB SQL using only returned columns and all user constraints.
        4. If the user asks to give, write, create, change, or generate SQL, call replace_active_query with the complete SQL.
        5. Use validate_sql or run_readonly_sql only when the user asks for validation, execution, results, or analysis.

        Do not answer an editor request with a generic example or a SQL code block. Complete it through the editor tool. After every
        tool result, continue the original task. If a tool fails, correct the call instead of repeating it unchanged. Never substitute
        a different algorithm (for example recursion when the user asked for level-by-level processing). Report success only after the
        required tool succeeds. When the requested outcome is truly complete, call complete_task. Do not call complete_task before the
        required action tool succeeds. If complete_task requests a review, call get_active_query, compare the actual SQL with the latest
        user request, and correct every missing requirement before completing again. Ask the user only for information that no available
        tool can obtain.
        """;
}
