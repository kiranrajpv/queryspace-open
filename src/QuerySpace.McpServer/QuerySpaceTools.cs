using ModelContextProtocol.Server;
using System.ComponentModel;

namespace QuerySpace.McpServer;

[McpServerToolType]
public sealed class QuerySpaceTools(QuerySpaceReader reader)
{
    [McpServerTool(Name = "list_datasets", ReadOnly = true), Description("List local QuerySpace datasets without exposing file paths or connection secrets.")]
    public object ListDatasets() => reader.ListDatasets();

    [McpServerTool(Name = "get_dataset_schema", ReadOnly = true), Description("Get the column schema for a QuerySpace dataset.")]
    public object GetDatasetSchema([Description("Dataset name from list_datasets.")] string datasetName) => reader.GetDatasetSchema(datasetName);

    [McpServerTool(Name = "sample_dataset", ReadOnly = true), Description("Return a bounded sample from a QuerySpace dataset.")]
    public object SampleDataset([Description("Dataset name from list_datasets.")] string datasetName, [Description("1-500 rows; default 20.")] int rows = 20) => reader.SampleDataset(datasetName, rows);

    [McpServerTool(Name = "list_sql_scripts", ReadOnly = true), Description("List top-level SQL scripts by file name only.")]
    public object ListSqlScripts() => reader.ListScripts();

    [McpServerTool(Name = "get_sql_script", ReadOnly = true), Description("Read a top-level QuerySpace SQL script by its listed file name.")]
    public object GetSqlScript([Description("File name from list_sql_scripts.")] string scriptName) => reader.GetScript(scriptName);

    [McpServerTool(Name = "run_readonly_sql", ReadOnly = true), Description("Run one SELECT/WITH query against dataset views. Mutations and external file/network access are blocked; output is capped at 500 rows.")]
    public object RunReadOnlySql([Description("One SELECT or WITH query using dataset names as tables.")] string sql, [Description("1-500 rows; default 100.")] int rows = 100) => reader.RunReadOnlySql(sql, rows);
}
