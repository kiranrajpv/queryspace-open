namespace QuerySpace;

public sealed class AiSettings
{
    public bool McpEnabled { get; set; }
    public bool AllowDatasetRead { get; set; } = true;
    public bool AllowEditorRead { get; set; } = true;
    public bool AllowEditorWrite { get; set; }
    public bool AllowSqlExecution { get; set; }
    public bool RequireSqlApproval { get; set; } = true;
    public bool AuditToolCalls { get; set; } = true;
    public int MaximumToolRows { get; set; } = 200;
    public int QueryTimeoutSeconds { get; set; } = 60;
    public string? DefaultModelKey { get; set; }
    public List<CloudModelProfile> CloudProfiles { get; set; } = new();
}

public sealed class CloudModelProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Cloud model";
    public string Provider { get; set; } = "OpenAI";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-10-21";
    public bool Enabled { get; set; } = true;
    public bool SupportsTools { get; set; } = true;
}

public sealed class AiModelOption
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string? ProfileId { get; init; }
    public bool IsLocal { get; init; }
    public bool SupportsTools { get; init; }
    public string CapabilityLabel => SupportsTools ? "Agent tools" : "Chat only";
    public string LocationLabel => IsLocal ? "Local" : "Cloud";
    public override string ToString() => $"{LocationLabel}: {DisplayName}";
}

public sealed class LocalModelInfo
{
    public string Alias { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string License { get; init; } = "";
    public string LicenseDescription { get; init; } = "";
    public int? FileSizeMb { get; init; }
    public long? ContextLength { get; init; }
    public bool SupportsTools { get; init; }
    public bool IsCached { get; init; }
    public string Device { get; init; } = "Unknown";
    public string ExecutionProvider { get; init; } = "Unknown";
    public string RuntimeLabel => ExecutionProvider.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        ? Device
        : $"{Device} / {ExecutionProvider}";
    public string Status => IsCached ? "Installed" : "Available";
    public string Size => FileSizeMb.HasValue ? $"{FileSizeMb.Value:N0} MB" : "Not specified";
}

public sealed record AgentToolDefinition(string Name, string Description, string JsonSchema, bool ChangesState = false);

public sealed record AgentToolCall(string Id, string Name, string Arguments);

public sealed class AgentRunResult
{
    public string Text { get; init; } = "";
    public int ToolCalls { get; init; }
    public bool EditorChanged { get; init; }
}
