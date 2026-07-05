using System.Data;
using System.Text.Json.Serialization;

namespace QuerySpace;

public sealed class DatasetInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? SourcePath { get; set; }
    public long? RowCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "odbc";
    public Dictionary<string, string> Config { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class ExtractQueryInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; set; } = "";
    public string? DatasetId { get; set; }
    public string Name { get; set; } = "";
    public string SqlPath { get; set; } = "";
    public long? RowCount { get; set; }
    public string Status { get; set; } = "saved";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class WorkspaceState
{
    public List<DatasetInfo> Datasets { get; set; } = new();
    public List<SavedConnection> Connections { get; set; } = new();
    public List<ExtractQueryInfo> ExtractQueries { get; set; } = new();
}

public sealed class ExtractProgress
{
    public string Message { get; init; } = "";
    public long? Rows { get; init; }
    public long? ExpectedRows { get; init; }
}

public sealed class QueryResult
{
    public DataTable Table { get; init; } = new();
    public int PreviewRows { get; init; }
    public long DisplayedRows => Table.Rows.Count;
}

public sealed class QualityColumn
{
    public string Name { get; init; } = "";
    public string DataType { get; init; } = "";
    public int NullCount { get; init; }
    public double NullPercent { get; init; }
    public int UniqueCount { get; init; }
}

public sealed class QualityReport
{
    public double QualityScore { get; init; }
    public int RowsAnalyzed { get; init; }
    public int NumericOutliers { get; init; }
    public List<QualityColumn> Columns { get; init; } = new();
}

public sealed class ExtractRequest
{
    public SavedConnection Connection { get; init; } = new();
    public string Password { get; init; } = "";
    public string DatasetName { get; init; } = "";
    public string Sql { get; init; } = "";
    public int BatchSize { get; init; } = 250_000;
    public long? ExpectedRows { get; init; }
}

public sealed class ParquetColumnInfo
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Nullable { get; init; } = "";
}

public sealed class DatasetDetails
{
    public string Path { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public long? RowCount { get; init; }
    public List<ParquetColumnInfo> Columns { get; init; } = new();
}

public sealed class MaterialCompareKeyMapping
{
    public string ReferenceColumn { get; init; } = "";
    public string TargetColumn { get; init; } = "";
}

public sealed class MaterialCompareRequest
{
    public DatasetInfo ReferenceDataset { get; init; } = new();
    public DatasetInfo TargetDataset { get; init; } = new();
    public DatasetInfo? ContextDataset { get; init; }
    public List<MaterialCompareKeyMapping> KeyMappings { get; init; } = new();
    public List<string> ExplainColumns { get; init; } = new();
    public bool NormalizeKeys { get; init; } = true;
    public bool SaveMissingAsDataset { get; init; }
    public string OutputDatasetName { get; init; } = "";
    public int PreviewRows { get; init; } = 1000;
}

public sealed class MaterialCompareResult
{
    public DataTable MissingRows { get; init; } = new();
    public DataTable Coverage { get; init; } = new();
    public DataTable ReasonSummary { get; init; } = new();
    public DataTable ColumnImpact { get; init; } = new();
    public string SqlUsed { get; init; } = "";
    public DatasetInfo? SavedDataset { get; init; }
    public long ReferenceRows { get; init; }
    public long FoundRows { get; init; }
    public long MissingRowsCount { get; init; }
}
