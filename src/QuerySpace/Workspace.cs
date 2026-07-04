using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuerySpace;

public sealed class Workspace
{
    private const string AppName = "QuerySpace";
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();

    public string Root { get; private set; }
    public string DatasetsFolder => Path.Combine(Root, "datasets");
    public string ScriptsFolder => Path.Combine(Root, "sql");
    public string ExportsFolder => Path.Combine(Root, "exports");
    public string SecretsFolder => Path.Combine(Root, ".secrets");
    private string MetadataPath => Path.Combine(Root, "queryspace.workspace.json");
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName,
        "settings.json");

    public WorkspaceState State { get; private set; } = new();

    public Workspace()
    {
        Root = LoadConfiguredRoot() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName,
            "workspace");
        EnsureFolders();
        Load();
    }

    public void SetRoot(string root)
    {
        Root = root;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { workspace = root }, _jsonOptions));
        EnsureFolders();
        Load();
    }

    public static string SafeName(string value)
    {
        var cleaned = new string(value.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
        cleaned = string.Join("_", cleaned.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "dataset" : cleaned;
    }

    public string EnsureDatasetNameAvailable(string name, string? excludeId = null)
    {
        lock (_syncRoot)
        {
            var safe = SafeName(name);
            if (State.Datasets.Any(d => d.Name.Equals(safe, StringComparison.OrdinalIgnoreCase) && d.Id != excludeId))
            {
                throw new InvalidOperationException($"Dataset name \"{safe}\" already exists. Delete the existing dataset and re-create it, or save with a different dataset name.");
            }
            return safe;
        }
    }

    public DatasetInfo AddDataset(string name, string path, string? sourcePath, long? rowCount)
    {
        lock (_syncRoot)
        {
            var safe = EnsureDatasetNameAvailable(name);
            var dataset = new DatasetInfo { Name = safe, Path = path, SourcePath = sourcePath, RowCount = rowCount };
            State.Datasets.Add(dataset);
            Save();
            return dataset;
        }
    }

    public void RenameDataset(DatasetInfo dataset, string newName)
    {
        lock (_syncRoot)
        {
            dataset.Name = EnsureDatasetNameAvailable(newName, dataset.Id);
            Save();
        }
    }

    public void DeleteDataset(DatasetInfo dataset)
    {
        lock (_syncRoot)
        {
            State.Datasets.RemoveAll(d => d.Id == dataset.Id);
            DeleteWorkspaceFileIfExists(dataset.Path, DatasetsFolder);
            Save();
        }
    }

    public void SaveScript(string name, string sql)
    {
        Directory.CreateDirectory(ScriptsFolder);
        var fileName = SafeName(Path.GetFileNameWithoutExtension(name)) + ".sql";
        File.WriteAllText(Path.Combine(ScriptsFolder, fileName), sql);
    }

    public IEnumerable<string> ListScripts()
    {
        Directory.CreateDirectory(ScriptsFolder);
        return Directory.EnumerateFiles(ScriptsFolder, "*.sql", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName);
    }

    public SavedConnection AddConnection(string name, string kind, Dictionary<string, string> config, string? secret)
    {
        lock (_syncRoot)
        {
            var connection = new SavedConnection { Name = name.Trim(), Kind = kind, Config = config };
            State.Connections.Add(connection);
            SaveSecret(connection.Id, secret ?? "");
            Save();
            return connection;
        }
    }

    public void UpdateConnection(SavedConnection connection, string name, string kind, Dictionary<string, string> config, string? secret)
    {
        lock (_syncRoot)
        {
            connection.Name = name.Trim();
            connection.Kind = kind;
            connection.Config = config;
            if (!string.IsNullOrEmpty(secret)) SaveSecret(connection.Id, secret);
            Save();
        }
    }

    public void DeleteConnection(SavedConnection connection)
    {
        lock (_syncRoot)
        {
            foreach (var query in State.ExtractQueries.Where(q => q.ConnectionId == connection.Id).ToList())
            {
                DeleteWorkspaceFileIfExists(query.SqlPath, ScriptsFolder);
                State.ExtractQueries.Remove(query);
            }
            State.Connections.RemoveAll(c => c.Id == connection.Id);
            DeleteSecret(connection.Id);
            Save();
        }
    }

    public string GetSecret(string connectionId)
    {
        var path = Path.Combine(SecretsFolder, connectionId + ".bin");
        if (!File.Exists(path)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public ExtractQueryInfo RecordExtractQuery(string connectionId, string? datasetId, string datasetName, string sql, long? rowCount, string status)
    {
        lock (_syncRoot)
        {
            var folder = Path.Combine(ScriptsFolder, "connections", connectionId);
            Directory.CreateDirectory(folder);
            var name = SafeName(datasetName);
            var path = Path.Combine(folder, name + ".sql");
            File.WriteAllText(path, sql);
            var query = new ExtractQueryInfo
            {
                ConnectionId = connectionId,
                DatasetId = datasetId,
                Name = name,
                SqlPath = path,
                RowCount = rowCount,
                Status = status
            };
            State.ExtractQueries.Add(query);
            Save();
            return query;
        }
    }

    public void DeleteExtractQuery(ExtractQueryInfo query)
    {
        lock (_syncRoot)
        {
            DeleteWorkspaceFileIfExists(query.SqlPath, ScriptsFolder);
            State.ExtractQueries.RemoveAll(q => q.Id == query.Id);
            Save();
        }
    }

    public void Save()
    {
        lock (_syncRoot)
        {
            EnsureFolders();
            File.WriteAllText(MetadataPath, JsonSerializer.Serialize(State, _jsonOptions));
        }
    }

    public List<DatasetInfo> SnapshotDatasets()
    {
        lock (_syncRoot)
        {
            return State.Datasets.ToList();
        }
    }

    public List<SavedConnection> SnapshotConnections()
    {
        lock (_syncRoot)
        {
            return State.Connections.ToList();
        }
    }

    public List<ExtractQueryInfo> SnapshotExtractQueries()
    {
        lock (_syncRoot)
        {
            return State.ExtractQueries.ToList();
        }
    }

    private void Load()
    {
        EnsureFolders();
        State = File.Exists(MetadataPath)
            ? JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(MetadataPath)) ?? new WorkspaceState()
            : new WorkspaceState();
    }

    private void EnsureFolders()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DatasetsFolder);
        Directory.CreateDirectory(ScriptsFolder);
        Directory.CreateDirectory(ExportsFolder);
        Directory.CreateDirectory(SecretsFolder);
    }

    private void SaveSecret(string connectionId, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        Directory.CreateDirectory(SecretsFolder);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(SecretsFolder, connectionId + ".bin"), bytes);
    }

    private void DeleteSecret(string connectionId)
    {
        var path = Path.Combine(SecretsFolder, connectionId + ".bin");
        DeleteWorkspaceFileIfExists(path, SecretsFolder);
    }

    private static void DeleteWorkspaceFileIfExists(string path, string allowedFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        var fullAllowedFolder = Path.GetFullPath(allowedFolder);
        if (!fullAllowedFolder.EndsWith(Path.DirectorySeparatorChar))
        {
            fullAllowedFolder += Path.DirectorySeparatorChar;
        }
        if (!fullPath.StartsWith(fullAllowedFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete a file outside the QuerySpace workspace: {fullPath}");
        }
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    private static string? LoadConfiguredRoot()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("workspace", out var value) ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
