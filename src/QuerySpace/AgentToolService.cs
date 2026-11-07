using DuckDB.NET.Data;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace QuerySpace;

public sealed partial class AgentToolService
{
    private const int HardMaximumRows = 500;
    private readonly Workspace _workspace;
    private readonly AiSettingsStore _settingsStore;
    private readonly Func<string> _getActiveSql;
    private readonly Action<string> _insertSql;
    private readonly Action<string> _replaceSql;
    private readonly object _runStateSync = new();
    private readonly HashSet<string> _inspectedDatasets = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Activity;

    public static string ActivityMessage(string toolName) => toolName.ToLowerInvariant() switch
    {
        "list_datasets" => "Finding available datasets...",
        "get_dataset_schema" => "Reading the dataset schema...",
        "sample_dataset" => "Inspecting sample data...",
        "validate_sql" => "Validating the generated SQL...",
        "run_readonly_sql" => "Running a bounded read-only query...",
        "get_active_query" => "Reading the active Query editor...",
        "insert_sql_in_editor" => "Adding SQL to the Query editor...",
        "replace_active_query" => "Updating the Query editor...",
        _ => $"Running {toolName}..."
    };

    public AgentToolService(
        Workspace workspace,
        AiSettingsStore settingsStore,
        Func<string> getActiveSql,
        Action<string> insertSql,
        Action<string> replaceSql)
    {
        _workspace = workspace;
        _settingsStore = settingsStore;
        _getActiveSql = getActiveSql;
        _insertSql = insertSql;
        _replaceSql = replaceSql;
    }

    public IReadOnlyList<AgentToolDefinition> Definitions { get; } =
    [
        new("list_datasets", "Use when you need to discover which QuerySpace datasets are available to the user and their row counts.", ObjectSchema()),
        new("get_dataset_schema", "Use when you need exact column names and DuckDB types for an available dataset before generating or executing SQL.", ObjectSchema(("datasetName", "string", "Exact dataset name returned by list_datasets"))),
        new("sample_dataset", "Use only when schema is insufficient and you need a bounded sample of actual values from an available dataset.", ObjectSchema(("datasetName", "string", "Exact dataset name"), ("rows", "integer", "Number of rows, maximum 100"))),
        new("validate_sql", "Validate exactly one read-only DuckDB SELECT/WITH statement without executing its result. A single trailing semicolon is allowed.", ObjectSchema(("sql", "string", "One DuckDB SELECT/WITH statement"))),
        new("run_readonly_sql", "Execute exactly one bounded read-only DuckDB SELECT/WITH statement. Use when the user asks to run/analyze data or when results are needed for the task; do not use merely to give or write SQL for the editor.", ObjectSchema(("sql", "string", "One DuckDB SELECT/WITH statement"), ("rows", "integer", "Maximum result rows"))),
        new("get_active_query", "Use when the user refers to the current, active, existing, or previously generated SQL and you need its exact editor contents.", ObjectSchema()),
        new("insert_sql_in_editor", "Insert SQL at the active editor cursor. Use when the user's intended outcome is to add SQL without replacing existing editor content.", ObjectSchema(("sql", "string", "SQL to insert")), true),
        new("replace_active_query", "Replace the active editor with a complete SQL query. Use when the user asks to give/write/create SQL as the editor result or asks to revise the current query.", ObjectSchema(("sql", "string", "Complete replacement SQL")), true)
    ];

    public void BeginAgentRun()
    {
        lock (_runStateSync) _inspectedDatasets.Clear();
    }

    public async Task<string> ExecuteAsync(AgentToolCall call, CancellationToken token)
    {
        var settings = _settingsStore.Load();
        if (!settings.McpEnabled) throw new InvalidOperationException("QuerySpace MCP/AI tools are disabled in AI Settings.");
        using var arguments = ParseArguments(call.Arguments);
        Activity?.Invoke($"{DateTime.Now:T}  → {call.Name}");
        _settingsStore.Audit("tool.request", new { call.Name, call.Arguments });

        string result;
        try
        {
            result = call.Name switch
            {
                "list_datasets" => ListDatasets(settings),
                "get_dataset_schema" => await GetSchemaAsync(RequiredString(arguments, "datasetName"), settings, token),
                "sample_dataset" => await SampleDatasetAsync(RequiredString(arguments, "datasetName"), OptionalInt(arguments, "rows", 20), settings, token),
                "validate_sql" => await ValidateSqlAsync(RequiredString(arguments, "sql"), settings, token),
                "run_readonly_sql" => await RunSqlAsync(RequiredString(arguments, "sql"), OptionalInt(arguments, "rows", settings.MaximumToolRows), settings, token),
                "get_active_query" => GetActiveQuery(settings),
                "insert_sql_in_editor" => await ChangeEditorAsync(RequiredString(arguments, "sql"), settings, replace: false, token),
                "replace_active_query" => await ChangeEditorAsync(RequiredString(arguments, "sql"), settings, replace: true, token),
                _ => throw new InvalidOperationException($"Unknown QuerySpace tool '{call.Name}'.")
            };
        }
        catch (Exception ex)
        {
            Activity?.Invoke($"{DateTime.Now:T}  ✕ {call.Name}: {ex.Message}");
            _settingsStore.Audit("tool.error", new { call.Name, error = ex.Message });
            throw;
        }

        Activity?.Invoke($"{DateTime.Now:T}  ✓ {call.Name} ({result.Length:N0} characters)");
        _settingsStore.Audit("tool.response", new { call.Name, length = result.Length });
        return result.Length <= 250_000 ? result : result[..250_000] + "\n{\"truncated\":true}";
    }

    private string ListDatasets(AiSettings settings)
    {
        EnsureDatasetRead(settings);
        var rows = ExposedDatasets().Select(d => new { d.Name, d.RowCount, d.CreatedAt }).ToList();
        return JsonSerializer.Serialize(new { datasets = rows });
    }

    private async Task<string> GetSchemaAsync(string name, AiSettings settings, CancellationToken token)
    {
        EnsureDatasetRead(settings);
        var dataset = FindExposedDataset(name);
        var result = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var connection = OpenRestrictedConnection();
            return JsonSerializer.Serialize(new
            {
                dataset = dataset.Name,
                columns = QueryRows(connection, $"DESCRIBE SELECT * FROM {Quote(dataset.Name)}", 1_000, token)
            });
        }, token);
        lock (_runStateSync) _inspectedDatasets.Add(dataset.Name);
        return result;
    }

    private async Task<string> SampleDatasetAsync(string name, int requestedRows, AiSettings settings, CancellationToken token)
    {
        EnsureDatasetRead(settings);
        var dataset = FindExposedDataset(name);
        var limit = Math.Clamp(requestedRows, 1, Math.Min(100, settings.MaximumToolRows));
        return await Task.Run(() =>
        {
            using var connection = OpenRestrictedConnection();
            return JsonSerializer.Serialize(new
            {
                dataset = dataset.Name,
                rowLimit = limit,
                rows = QueryRows(connection, $"SELECT * FROM {Quote(dataset.Name)} LIMIT {limit}", limit, token)
            });
        }, token);
    }

    private async Task<string> ValidateSqlAsync(string sql, AiSettings settings, CancellationToken token)
    {
        EnsureDatasetRead(settings);
        ValidateReadOnlySql(sql);
        RejectHiddenDatasetReferences(sql);
        return await Task.Run(() =>
        {
            using var connection = OpenRestrictedConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN " + TrimSemicolon(sql);
            command.ExecuteNonQuery();
            return JsonSerializer.Serialize(new { valid = true });
        }, token);
    }

    private async Task<string> RunSqlAsync(string sql, int requestedRows, AiSettings settings, CancellationToken token)
    {
        EnsureDatasetRead(settings);
        if (!settings.AllowSqlExecution) throw new InvalidOperationException("AI SQL execution is disabled in AI Settings.");
        ValidateReadOnlySql(sql);
        RejectHiddenDatasetReferences(sql);
        var limit = Math.Clamp(requestedRows, 1, Math.Min(HardMaximumRows, settings.MaximumToolRows));
        if (settings.RequireSqlApproval && !ApproveSql(sql))
            throw new InvalidOperationException("The user rejected SQL execution.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.QueryTimeoutSeconds, 5, 600)));
        return await Task.Run(() =>
        {
            using var connection = OpenRestrictedConnection();
            var bounded = $"SELECT * FROM ({TrimSemicolon(sql)}) AS queryspace_ai_result LIMIT {limit}";
            var rows = QueryRows(connection, bounded, limit, timeout.Token);
            return JsonSerializer.Serialize(new { rowLimit = limit, returnedRows = rows.Count, rows });
        }, timeout.Token);
    }

    private string GetActiveQuery(AiSettings settings)
    {
        if (!settings.AllowEditorRead) throw new InvalidOperationException("AI editor read access is disabled in AI Settings.");
        return Application.Current.Dispatcher.Invoke(() => JsonSerializer.Serialize(new { sql = _getActiveSql() }));
    }

    private async Task<string> ChangeEditorAsync(string sql, AiSettings settings, bool replace, CancellationToken token)
    {
        if (!settings.AllowEditorWrite) throw new InvalidOperationException("AI editor write access is disabled in AI Settings.");
        await ValidateEditorSqlAsync(sql, settings, token);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (replace) _replaceSql(sql); else _insertSql(sql);
        });
        return JsonSerializer.Serialize(new { success = true, action = replace ? "replaced" : "inserted" });
    }

    private async Task ValidateEditorSqlAsync(string sql, AiSettings settings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("The editor SQL cannot be empty.");
        var datasets = ExposedDatasets();
        var referenced = datasets.Where(dataset => ContainsIdentifier(sql, dataset.Name)).ToList();
        if (referenced.Count == 0)
            throw new InvalidOperationException("The generated SQL does not reference an available QuerySpace dataset. Call list_datasets and get_dataset_schema, then use the exact dataset name.");

        List<string> missing;
        lock (_runStateSync)
            missing = referenced.Where(dataset => !_inspectedDatasets.Contains(dataset.Name)).Select(dataset => dataset.Name).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Inspect the schema before editing SQL. Call get_dataset_schema for: {string.Join(", ", missing)}.");

        var candidate = TrimSemicolon(sql);
        var first = FirstWord().Match(candidate).Groups[1].Value;
        if (!first.Equals("select", StringComparison.OrdinalIgnoreCase) && !first.Equals("with", StringComparison.OrdinalIgnoreCase))
            return;

        ValidateReadOnlySql(candidate);
        RejectHiddenDatasetReferences(candidate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var validationSeconds = Math.Clamp(settings.QueryTimeoutSeconds, 5, 15);
        timeout.CancelAfter(TimeSpan.FromSeconds(validationSeconds));
        try
        {
            using var connection = OpenRestrictedConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN " + candidate;
            using var cancellation = timeout.Token.Register(() =>
            {
                try { command.Cancel(); } catch { }
            });
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"DuckDB SQL validation exceeded {validationSeconds} seconds and was cancelled. The SQL was not written to the editor. Simplify or correct the generated query.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"DuckDB rejected the generated SQL, so it was not written to the editor: {ex.Message}", ex);
        }
    }

    private bool ApproveSql(string sql) => Application.Current.Dispatcher.Invoke(() =>
        MessageBox.Show(Application.Current.MainWindow,
            $"The AI assistant wants to execute this read-only SQL:\n\n{sql}",
            "Approve AI SQL execution", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    private DuckDBConnection OpenRestrictedConnection()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            foreach (var dataset in ExposedDatasets())
            {
                var fullPath = ValidateDatasetPath(dataset);
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE VIEW {Quote(dataset.Name)} AS SELECT * FROM read_parquet('{Literal(fullPath)}')";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private List<Dictionary<string, object?>> QueryRows(DuckDBConnection connection, string sql, int limit, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        var result = new List<Dictionary<string, object?>>();
        while (reader.Read() && result.Count < limit)
        {
            token.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = JsonValue(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            result.Add(row);
        }
        return result;
    }

    private List<DatasetInfo> ExposedDatasets() => _workspace.SnapshotDatasets()
        .Where(d => d.ExposeToMcp)
        .Where(d => File.Exists(d.Path))
        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private DatasetInfo FindExposedDataset(string name) => ExposedDatasets()
        .SingleOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Dataset '{name}' is not enabled for MCP/AI access.");

    private string ValidateDatasetPath(DatasetInfo dataset)
    {
        var fullPath = Path.GetFullPath(dataset.Path);
        var allowed = Path.GetFullPath(_workspace.DatasetsFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Dataset '{dataset.Name}' has an invalid workspace path.");
        return fullPath;
    }

    private void RejectHiddenDatasetReferences(string sql)
    {
        foreach (var hidden in _workspace.SnapshotDatasets().Where(d => !d.ExposeToMcp))
        {
            if (Regex.IsMatch(sql, $@"(?<![A-Za-z0-9_]){Regex.Escape(hidden.Name)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase))
                throw new InvalidOperationException($"Dataset '{hidden.Name}' is not enabled for MCP/AI access.");
        }
    }

    private static bool ContainsIdentifier(string sql, string identifier) =>
        Regex.IsMatch(sql, $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);

    private static void EnsureDatasetRead(AiSettings settings)
    {
        if (!settings.AllowDatasetRead) throw new InvalidOperationException("AI dataset access is disabled in AI Settings.");
    }

    private static JsonDocument ParseArguments(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException ex) { throw new InvalidOperationException("The model returned invalid tool arguments.", ex); }
    }

    private static string RequiredString(JsonDocument document, string property)
    {
        if (document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw new InvalidOperationException($"Tool argument '{property}' is required.");
    }

    private static int OptionalInt(JsonDocument document, string property, int fallback) =>
        document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static string ObjectSchema(params (string Name, string Type, string Description)[] properties)
    {
        var schema = new
        {
            type = "object",
            properties = properties.ToDictionary(p => p.Name, p => new { type = p.Type, description = p.Description }),
            required = properties.Where(p => p.Name is not "rows").Select(p => p.Name).ToArray(),
            additionalProperties = false
        };
        return JsonSerializer.Serialize(schema);
    }

    private static void ValidateReadOnlySql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 100_000) throw new InvalidOperationException("A reasonably sized SQL query is required.");
        var clean = Strings().Replace(BlockComments().Replace(LineComments().Replace(sql, " "), " "), "''").Trim();
        if (clean.EndsWith(';')) clean = clean[..^1].TrimEnd();
        if (clean.Contains(';')) throw new InvalidOperationException("Only one SQL statement is allowed.");
        var first = FirstWord().Match(clean).Groups[1].Value;
        if (!first.Equals("select", StringComparison.OrdinalIgnoreCase) && !first.Equals("with", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT or WITH queries are allowed.");
        var blocked = BlockedKeywords().Match(clean);
        if (blocked.Success) throw new InvalidOperationException($"SQL keyword '{blocked.Value.ToUpperInvariant()}' is not allowed.");
        var external = ExternalFunctions().Match(clean);
        if (external.Success) throw new InvalidOperationException($"External-access function '{external.Value}' is not allowed.");
    }

    private static object? JsonValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime date => date.ToString("O"),
        DateTimeOffset date => date.ToString("O"),
        TimeSpan span => span.ToString(),
        Guid guid => guid.ToString(),
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Literal(string value) => value.Replace("'", "''");
    private static string TrimSemicolon(string value) => value.Trim().TrimEnd(';');

    [GeneratedRegex(@"^\s*([a-z]+)", RegexOptions.IgnoreCase)] private static partial Regex FirstWord();
    [GeneratedRegex(@"\b(insert|update|delete|merge|create|drop|alter|copy|export|attach|detach|install|load|pragma|call|set|reset|use|vacuum|checkpoint|force|import)\b", RegexOptions.IgnoreCase)] private static partial Regex BlockedKeywords();
    [GeneratedRegex(@"\b(read_[a-z0-9_]+|[a-z0-9_]+_scan|glob|query|query_table|getenv|current_setting|duckdb_settings|database_list|which_secret|duckdb_secrets|http_get|http_post|httpfs)\b", RegexOptions.IgnoreCase)] private static partial Regex ExternalFunctions();
    [GeneratedRegex(@"--[^\r\n]*")] private static partial Regex LineComments();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex BlockComments();
    [GeneratedRegex(@"'(?:''|[^'])*'")] private static partial Regex Strings();
}
