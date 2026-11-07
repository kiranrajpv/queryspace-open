using DuckDB.NET.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuerySpace.McpServer;

public sealed partial class QuerySpaceReader
{
    private const int MaxRows = 500;
    private const int MaxScriptChars = 200_000;
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuerySpace", "settings.json");
    private static readonly string AiSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuerySpace", "ai-settings.json");

    public object ListDatasets()
    {
        EnsureDatasetReadAllowed();
        var workspace = LoadWorkspace();
        return workspace.Datasets.Where(d => d.ExposeToMcp && TryResolveDataset(workspace, d, out _))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new { d.Name, d.RowCount, d.CreatedAt }).ToList();
    }

    public object GetDatasetSchema(string datasetName)
    {
        EnsureDatasetReadAllowed();
        var (workspace, dataset) = FindDataset(datasetName);
        using var connection = OpenConnection(workspace);
        return new { dataset = dataset.Name, columns = QueryRows(connection, $"DESCRIBE SELECT * FROM {Quote(dataset.Name)}") };
    }

    public object SampleDataset(string datasetName, int rows)
    {
        var settings = EnsureDatasetReadAllowed();
        var limit = Limit(rows, 20, settings.MaximumToolRows);
        var (workspace, dataset) = FindDataset(datasetName);
        using var connection = OpenConnection(workspace);
        return new { rowLimit = limit, rows = QueryRows(connection, $"SELECT * FROM {Quote(dataset.Name)} LIMIT {limit}") };
    }

    public object ListScripts()
    {
        EnsureEditorReadAllowed();
        var folder = LoadWorkspace().ScriptsFolder;
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.sql", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    public object GetScript(string scriptName)
    {
        EnsureEditorReadAllowed();
        var folder = LoadWorkspace().ScriptsFolder;
        var name = Path.GetFileName(scriptName);
        if (!name.Equals(scriptName, StringComparison.Ordinal) || !name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a file name returned by list_sql_scripts.");
        var path = Path.Combine(folder, name);
        EnsureWithin(path, folder);
        if (!File.Exists(path)) throw new InvalidOperationException($"SQL script '{name}' was not found.");
        var sql = File.ReadAllText(path);
        if (sql.Length > MaxScriptChars) throw new InvalidOperationException("SQL script is too large to return safely.");
        return new { name, sql };
    }

    public object RunReadOnlySql(string sql, int rows)
    {
        var settings = EnsureDatasetReadAllowed();
        if (!settings.AllowSqlExecution)
            throw new InvalidOperationException("MCP SQL execution is disabled in QuerySpace AI Settings.");
        if (settings.RequireSqlApproval)
            throw new InvalidOperationException("Per-query approval is enabled. External MCP SQL cannot display QuerySpace's approval dialog; disable per-query approval explicitly to allow external MCP SQL execution.");
        var limit = Limit(rows, 100, settings.MaximumToolRows);
        ValidateSql(sql);
        using var connection = OpenConnection(LoadWorkspace());
        var bounded = $"SELECT * FROM ({sql.Trim().TrimEnd(';')}) AS queryspace_mcp_result LIMIT {limit}";
        return new { rowLimit = limit, rows = QueryRows(connection, bounded) };
    }

    private static List<Dictionary<string, object?>> QueryRows(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = JsonValue(reader.IsDBNull(i) ? null : reader.GetValue(i));
            result.Add(row);
        }
        return result;
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
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static DuckDBConnection OpenConnection(WorkspaceSnapshot workspace)
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            foreach (var dataset in workspace.Datasets.Where(d => d.ExposeToMcp))
            {
                if (!TryResolveDataset(workspace, dataset, out var path)) continue;
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE VIEW {Quote(dataset.Name)} AS SELECT * FROM read_parquet('{Literal(path)}')";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void ValidateSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 100_000) throw new InvalidOperationException("A reasonably sized SQL query is required.");
        var clean = Strings().Replace(BlockComments().Replace(LineComments().Replace(sql, " "), " "), "''");
        if (clean.Contains(';')) throw new InvalidOperationException("Only one SQL statement is allowed.");
        var first = FirstWord().Match(clean).Groups[1].Value;
        if (!first.Equals("select", StringComparison.OrdinalIgnoreCase) && !first.Equals("with", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT or WITH queries are allowed.");
        var blocked = BlockedKeywords().Match(clean);
        if (blocked.Success) throw new InvalidOperationException($"SQL keyword '{blocked.Value.ToUpperInvariant()}' is not allowed.");
        var external = ExternalFunctions().Match(clean);
        if (external.Success) throw new InvalidOperationException($"External-access function '{external.Value}' is not allowed.");
    }

    private static WorkspaceSnapshot LoadWorkspace()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuerySpace", "workspace");
        if (File.Exists(SettingsPath))
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (settings.RootElement.TryGetProperty("workspace", out var value) && !string.IsNullOrWhiteSpace(value.GetString())) root = value.GetString()!;
        }
        root = Path.GetFullPath(root);
        var metadata = Path.Combine(root, "queryspace.workspace.json");
        if (!File.Exists(metadata)) throw new InvalidOperationException("QuerySpace workspace metadata was not found. Configure a workspace in QuerySpace first.");
        using var document = JsonDocument.Parse(File.ReadAllText(metadata));
        var datasets = document.RootElement.TryGetProperty("Datasets", out var valueElement)
            ? JsonSerializer.Deserialize<List<DatasetState>>(valueElement, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [] : [];
        return new(root, Path.Combine(root, "datasets"), Path.Combine(root, "sql"), datasets);
    }

    private static (WorkspaceSnapshot Workspace, DatasetState Dataset) FindDataset(string name)
    {
        var workspace = LoadWorkspace();
        var dataset = workspace.Datasets.SingleOrDefault(d => d.ExposeToMcp && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Dataset '{name}' was not found or is not enabled for MCP access.");
        if (!TryResolveDataset(workspace, dataset, out _)) throw new InvalidOperationException($"Dataset '{dataset.Name}' has no valid workspace Parquet file.");
        return (workspace, dataset);
    }

    private static bool TryResolveDataset(WorkspaceSnapshot workspace, DatasetState dataset, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(dataset.Name) || string.IsNullOrWhiteSpace(dataset.Path)) return false;
        var candidate = Path.GetFullPath(dataset.Path);
        try { EnsureWithin(candidate, workspace.DatasetsFolder); } catch { return false; }
        if (!candidate.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return false;
        path = candidate;
        return true;
    }

    private static void EnsureWithin(string path, string folder)
    {
        var fullPath = Path.GetFullPath(path);
        var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to access a file outside the QuerySpace workspace.");
    }

    private static int Limit(int requested, int fallback, int configuredMaximum)
    {
        var maximum = Math.Clamp(configuredMaximum <= 0 ? MaxRows : configuredMaximum, 1, MaxRows);
        return Math.Clamp(requested <= 0 ? fallback : requested, 1, maximum);
    }

    private static AiAccessSettings EnsureDatasetReadAllowed()
    {
        var settings = LoadAiAccessSettings();
        if (!settings.AllowDatasetRead)
            throw new InvalidOperationException("MCP dataset access is disabled in QuerySpace AI Settings.");
        return settings;
    }

    private static AiAccessSettings EnsureEditorReadAllowed()
    {
        var settings = LoadAiAccessSettings();
        if (!settings.AllowEditorRead)
            throw new InvalidOperationException("MCP SQL script access is disabled in QuerySpace AI Settings.");
        return settings;
    }

    private static AiAccessSettings LoadAiAccessSettings()
    {
        if (!File.Exists(AiSettingsPath)) throw new InvalidOperationException("QuerySpace MCP access is disabled. Enable it in AI Settings.");
        try
        {
            var settings = JsonSerializer.Deserialize<AiAccessSettings>(File.ReadAllText(AiSettingsPath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AiAccessSettings();
            if (!settings.McpEnabled)
                throw new InvalidOperationException("QuerySpace MCP access is disabled. Enable it in AI Settings.");
            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("QuerySpace AI settings could not be read safely.", ex);
        }
    }
    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Literal(string value) => value.Replace("'", "''");

    [GeneratedRegex(@"^\s*([a-z]+)", RegexOptions.IgnoreCase)] private static partial Regex FirstWord();
    [GeneratedRegex(@"\b(insert|update|delete|merge|create|drop|alter|copy|export|attach|detach|install|load|pragma|call|set|reset|use|vacuum|checkpoint|force|import)\b", RegexOptions.IgnoreCase)] private static partial Regex BlockedKeywords();
    [GeneratedRegex(@"\b(read_[a-z0-9_]+|[a-z0-9_]+_scan|glob|query|query_table|getenv|current_setting|duckdb_settings|database_list|which_secret|duckdb_secrets|http_get|http_post|httpfs)\b", RegexOptions.IgnoreCase)] private static partial Regex ExternalFunctions();
    [GeneratedRegex(@"--[^\r\n]*")] private static partial Regex LineComments();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex BlockComments();
    [GeneratedRegex(@"'(?:''|[^'])*'")] private static partial Regex Strings();

    private sealed record WorkspaceSnapshot(string Root, string DatasetsFolder, string ScriptsFolder, List<DatasetState> Datasets);
    private sealed class DatasetState
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public long? RowCount { get; set; }
        public bool ExposeToMcp { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class AiAccessSettings
    {
        public bool McpEnabled { get; set; }
        public bool AllowDatasetRead { get; set; }
        public bool AllowEditorRead { get; set; }
        public bool AllowSqlExecution { get; set; }
        public bool RequireSqlApproval { get; set; } = true;
        public int MaximumToolRows { get; set; } = MaxRows;
    }
}
