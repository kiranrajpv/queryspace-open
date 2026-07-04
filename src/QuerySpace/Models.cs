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
    public int BatchSize { get; init; } = 50_000;
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
