using DuckDB.NET.Data;
using ExcelDataReader;
using Microsoft.Data.SqlClient;
using Parquet;
using Parquet.Schema;
using Snowflake.Data.Client;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace QuerySpace;

public sealed class DataEngine
{
    private readonly Workspace _workspace;
    private DuckDBConnection _memoryConnection = new("Data Source=:memory:");
    private readonly SemaphoreSlim _memoryConnectionLock = new(1, 1);

    public DataEngine(Workspace workspace)
    {
        _workspace = workspace;
        _memoryConnection.Open();
    }

    public async Task<DatasetInfo> ImportFileAsync(string sourcePath, string? datasetName, IProgress<string>? progress, CancellationToken token)
    {
        var name = _workspace.EnsureDatasetNameAvailable(datasetName ?? Path.GetFileNameWithoutExtension(sourcePath));
        var finalPath = Path.Combine(_workspace.DatasetsFolder, name + ".parquet");
        if (File.Exists(finalPath)) throw new InvalidOperationException($"Parquet file already exists: {finalPath}");
        var partialPath = finalPath + ".partial";
        if (File.Exists(partialPath)) File.Delete(partialPath);

        try
        {
            progress?.Report("Importing file...");
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is ".xlsx" or ".xls")
            {
                var csv = Path.Combine(Path.GetTempPath(), $"queryspace-{Guid.NewGuid():N}.csv");
                try
                {
                    WriteFirstExcelSheetToCsv(sourcePath, csv);
                    await CopyCsvToParquetAsync(csv, partialPath, token);
                }
                finally
                {
                    if (File.Exists(csv)) File.Delete(csv);
                }
            }
            else if (ext == ".csv")
            {
                await CopyCsvToParquetAsync(sourcePath, partialPath, token);
            }
            else if (ext == ".parquet")
            {
                File.Copy(sourcePath, partialPath);
            }
            else
            {
                throw new InvalidOperationException("Supported imports are CSV, XLSX, XLS, and Parquet files.");
            }

            token.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            var rowCount = await CountParquetRowsAsync(finalPath, token);
            return _workspace.AddDataset(name, finalPath, sourcePath, rowCount);
        }
        catch
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            throw;
        }
    }

    public async Task<QueryResult> ExecuteAsync(string sql, int previewRows, string mode, CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        var statements = SplitStatements(sql).ToList();
        if (statements.Count == 0) throw new InvalidOperationException("Enter a SQL statement.");
        if (mode.Equals("Read Only", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var statement in statements)
            {
                if (!IsReadOnly(statement)) throw new InvalidOperationException("Read Only mode allows SELECT/WITH/DESCRIBE/SHOW only.");
            }
        }

        var useMemoryConnection = mode.Equals("Memory", StringComparison.OrdinalIgnoreCase);
        if (useMemoryConnection)
        {
            await _memoryConnectionLock.WaitAsync(token);
        }

        var connection = useMemoryConnection ? _memoryConnection : NewOpenDuckConnection();
        try
        {
            RegisterDatasetViews(connection);
            DataTable? last = null;
            for (var i = 0; i < statements.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = i == statements.Count - 1
                    ? $"select * from ({TrimTrailingSemicolon(statements[i])}) q limit {Math.Max(1, previewRows)}"
                    : statements[i];
                try
                {
                    using var reader = command.ExecuteReader();
                    var table = new DataTable();
                    table.Load(reader);
                    last = table;
                }
                catch
                {
                    if (i == statements.Count - 1)
                    {
                        command.CommandText = statements[i];
                        command.ExecuteNonQuery();
                        last = new DataTable();
                        continue;
                    }
                    command.ExecuteNonQuery();
                }
            }
            return new QueryResult { Table = last ?? new DataTable(), PreviewRows = previewRows };
        }
        finally
        {
            if (!ReferenceEquals(connection, _memoryConnection)) connection.Dispose();
            if (useMemoryConnection) _memoryConnectionLock.Release();
        }
    }

    public async Task<ScriptExecutionSummary> ExecuteScriptSummaryAsync(string sql, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var statements = SplitStatements(sql).ToList();
            if (statements.Count == 0) throw new InvalidOperationException("SQL script is empty.");

            using var connection = NewOpenDuckConnection();
            RegisterDatasetViews(connection);

            for (var i = 0; i < statements.Count - 1; i++)
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = statements[i];
                try
                {
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
                catch
                {
                    command.ExecuteNonQuery();
                }
            }

            token.ThrowIfCancellationRequested();
            var last = TrimTrailingSemicolon(statements[^1]);
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $"select count(*) from ({last}) q";
            try
            {
                var count = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                return new ScriptExecutionSummary { RowCount = count, ReturnedRows = true };
            }
            catch
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = last;
                try
                {
                    using var reader = command.ExecuteReader();
                    long rows = 0;
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        rows++;
                    }
                    return new ScriptExecutionSummary { RowCount = rows, ReturnedRows = true };
                }
                catch
                {
                    command.ExecuteNonQuery();
                    return new ScriptExecutionSummary { ReturnedRows = false };
                }
            }
        }, token);
    }

    public async Task ExportResultAsync(string sql, string outputPath, ExportFormat format, IProgress<string>? progress, CancellationToken token)
    {
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var partialPath = outputPath + ".partial";
            if (File.Exists(partialPath)) File.Delete(partialPath);
            using var connection = NewOpenDuckConnection();
            RegisterDatasetViews(connection);
            var last = PrepareStatementsForExport(connection, sql, token);
            try
            {
                progress?.Report($"Exporting {format}...");
                switch (format)
                {
                    case ExportFormat.Csv:
                        CopyQueryToFile(connection, last, partialPath, "csv");
                        break;
                    case ExportFormat.Parquet:
                        CopyQueryToFile(connection, last, partialPath, "parquet");
                        break;
                    case ExportFormat.Excel:
                        WriteQueryToExcel(connection, last, partialPath, progress, token);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported export format: {format}");
                }

                token.ThrowIfCancellationRequested();
                File.Move(partialPath, outputPath, true);
            }
            finally
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
            }
        }, token);
    }

    public async Task ExportInMemoryObjectAsync(InMemoryObjectInfo obj, string outputPath, ExportFormat format, IProgress<string>? progress, CancellationToken token)
    {
        await _memoryConnectionLock.WaitAsync(token);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var partialPath = outputPath + ".partial";
                if (File.Exists(partialPath)) File.Delete(partialPath);
                try
                {
                    progress?.Report($"Exporting {obj.Name}...");
                    var sql = $"select * from {QuoteQualifiedName(obj.Schema, obj.Name)}";
                    switch (format)
                    {
                        case ExportFormat.Csv:
                            CopyQueryToFile(_memoryConnection, sql, partialPath, "csv");
                            break;
                        case ExportFormat.Parquet:
                            CopyQueryToFile(_memoryConnection, sql, partialPath, "parquet");
                            break;
                        case ExportFormat.Excel:
                            WriteQueryToExcel(_memoryConnection, sql, partialPath, progress, token);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported export format: {format}");
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(partialPath, outputPath, true);
                }
                finally
                {
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                }
            }, token);
        }
        finally
        {
            _memoryConnectionLock.Release();
        }
    }

    public async Task<DatasetInfo> ExtractAsync(ExtractRequest request, IProgress<ExtractProgress>? progress, CancellationToken token)
    {
        var name = _workspace.EnsureDatasetNameAvailable(request.DatasetName);
        var finalPath = Path.Combine(_workspace.DatasetsFolder, name + ".parquet");
        var partialParquet = finalPath + ".partial";
        if (File.Exists(finalPath)) throw new InvalidOperationException($"Parquet file already exists: {finalPath}");
        if (File.Exists(partialParquet)) File.Delete(partialParquet);

        try
        {
            progress?.Report(new ExtractProgress { Message = $"Connecting to {request.Connection.Kind}...", ExpectedRows = request.ExpectedRows });
            await using var connection = OpenDatabaseConnection(request.Connection, request.Password);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = request.Sql;
            command.CommandTimeout = 0;
            progress?.Report(new ExtractProgress { Message = "Executing base query...", ExpectedRows = request.ExpectedRows });
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            progress?.Report(new ExtractProgress { Message = "Writing Parquet row groups...", ExpectedRows = request.ExpectedRows });
            var rows = await WriteReaderToParquetAsync(reader, partialParquet, request.BatchSize, request.ExpectedRows, progress, token);
            if (request.ExpectedRows.HasValue && request.ExpectedRows.Value != rows)
            {
                throw new InvalidOperationException($"Expected {request.ExpectedRows.Value:N0} rows but extracted {rows:N0}. Partial output was discarded.");
            }
            File.Move(partialParquet, finalPath);
            var dataset = _workspace.AddDataset(name, finalPath, null, rows);
            _workspace.RecordExtractQuery(request.Connection.Id, dataset.Id, name, request.Sql, rows, "completed");
            return dataset;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsSqlServerUntrustedCertificateError(ex))
        {
            throw new InvalidOperationException(SqlServerCertificateHelp + "\n\n" + SqlServerConnectionDiagnostic(request.Connection.Config), ex);
        }
        finally
        {
            if (File.Exists(partialParquet)) File.Delete(partialParquet);
        }
    }

    public async Task<DatasetDetails> GetDatasetDetailsAsync(DatasetInfo dataset, CancellationToken token)
    {
        var details = new DatasetDetails
        {
            Path = dataset.Path,
            FileSizeBytes = File.Exists(dataset.Path) ? new FileInfo(dataset.Path).Length : 0,
            RowCount = dataset.RowCount
        };
        await Task.Run(() =>
        {
            using var connection = NewOpenDuckConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"describe select * from read_parquet('{EscapePath(dataset.Path)}')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                details.Columns.Add(new ParquetColumnInfo
                {
                    Name = reader.GetValue(0)?.ToString() ?? "",
                    Type = reader.GetValue(1)?.ToString() ?? "",
                    Nullable = reader.FieldCount > 2 ? reader.GetValue(2)?.ToString() ?? "" : ""
                });
            }
        }, token);
        return details;
    }

    public async Task<IReadOnlyList<InMemoryObjectInfo>> GetInMemoryObjectsAsync(CancellationToken token)
    {
        await _memoryConnectionLock.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                RegisterDatasetViews(_memoryConnection);
                var datasetAliases = _workspace.SnapshotDatasets()
                    .Select(d => Workspace.SafeName(d.Name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var objects = new List<InMemoryObjectInfo>();

                using (var command = _memoryConnection.CreateCommand())
                {
                    command.CommandText =
                        "select table_schema, table_name, table_type " +
                        "from information_schema.tables " +
                        "where table_schema not in ('information_schema', 'pg_catalog') " +
                        "order by table_schema, table_type, table_name";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var schema = reader.GetValue(0)?.ToString() ?? "main";
                        var name = reader.GetValue(1)?.ToString() ?? "";
                        var tableType = reader.GetValue(2)?.ToString() ?? "";
                        var isView = tableType.Contains("VIEW", StringComparison.OrdinalIgnoreCase);
                        if (isView && datasetAliases.Contains(name)) continue;

                        var isTemporary = schema.Contains("temp", StringComparison.OrdinalIgnoreCase) ||
                                          tableType.Contains("TEMP", StringComparison.OrdinalIgnoreCase);
                        long? rowCount = null;
                        if (!isView)
                        {
                            rowCount = TryCountObject(_memoryConnection, schema, name);
                        }

                        objects.Add(new InMemoryObjectInfo
                        {
                            Schema = schema,
                            Name = name,
                            ObjectType = isView
                                ? isTemporary ? "Temporary View" : "View"
                                : isTemporary ? "Temporary Table" : "Table",
                            IsTemporary = isTemporary,
                            RowCount = rowCount,
                            Detail = rowCount.HasValue ? $"({rowCount.Value:N0} rows)" : ""
                        });
                    }
                }

                AddOptionalMetadataRows(_memoryConnection, objects, "Macros / Functions",
                    "select coalesce(schema_name, 'main') as schema_name, function_name, function_type " +
                    "from duckdb_functions() " +
                    "where function_type ilike '%macro%' and coalesce(internal, false) = false " +
                    "order by schema_name, function_name",
                    "function_name", "schema_name", "function_type", token);

                AddOptionalMetadataRows(_memoryConnection, objects, "Index",
                    "select coalesce(schema_name, 'main') as schema_name, index_name, table_name " +
                    "from duckdb_indexes() order by schema_name, index_name",
                    "index_name", "schema_name", "table_name", token);

                AddOptionalMetadataRows(_memoryConnection, objects, "Sequence",
                    "select coalesce(schema_name, 'main') as schema_name, sequence_name, '' as detail " +
                    "from duckdb_sequences() order by schema_name, sequence_name",
                    "sequence_name", "schema_name", "detail", token);

                AddOptionalMetadataRows(_memoryConnection, objects, "Schema",
                    "select schema_name, schema_name as object_name, '' as detail " +
                    "from duckdb_schemas() " +
                    "where schema_name not in ('information_schema', 'pg_catalog') and coalesce(internal, false) = false " +
                    "order by schema_name",
                    "object_name", "schema_name", "detail", token);

                return objects
                    .OrderBy(o => ObjectSortOrder(o.ObjectType))
                    .ThenBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, token);
        }
        finally
        {
            _memoryConnectionLock.Release();
        }
    }

    public async Task<InMemoryObjectDetails> GetInMemoryObjectDetailsAsync(InMemoryObjectInfo obj, CancellationToken token)
    {
        await _memoryConnectionLock.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var details = new InMemoryObjectDetails { Object = obj, Definition = TryGetObjectDefinition(_memoryConnection, obj) };
                if (obj.ObjectType.Contains("Table", StringComparison.OrdinalIgnoreCase) ||
                    obj.ObjectType.Contains("View", StringComparison.OrdinalIgnoreCase))
                {
                    using var command = _memoryConnection.CreateCommand();
                    command.CommandText = $"describe select * from {QuoteQualifiedName(obj.Schema, obj.Name)}";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        details.Columns.Add(new ParquetColumnInfo
                        {
                            Name = reader.GetValue(0)?.ToString() ?? "",
                            Type = reader.GetValue(1)?.ToString() ?? "",
                            Nullable = reader.FieldCount > 2 ? reader.GetValue(2)?.ToString() ?? "" : ""
                        });
                    }
                }
                return details;
            }, token);
        }
        finally
        {
            _memoryConnectionLock.Release();
        }
    }

    public async Task DropInMemoryObjectAsync(InMemoryObjectInfo obj, CancellationToken token)
    {
        await _memoryConnectionLock.WaitAsync(token);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var command = _memoryConnection.CreateCommand();
                var qualifiedName = QuoteQualifiedName(obj.Schema, obj.Name);
                command.CommandText = obj.ObjectType switch
                {
                    var t when t.Contains("View", StringComparison.OrdinalIgnoreCase) => $"drop view if exists {qualifiedName}",
                    var t when t.Contains("Table", StringComparison.OrdinalIgnoreCase) => $"drop table if exists {qualifiedName}",
                    var t when t.Contains("Macro", StringComparison.OrdinalIgnoreCase) || t.Contains("Function", StringComparison.OrdinalIgnoreCase) => $"drop macro if exists {qualifiedName}",
                    var t when t.Contains("Index", StringComparison.OrdinalIgnoreCase) => $"drop index if exists {qualifiedName}",
                    var t when t.Contains("Sequence", StringComparison.OrdinalIgnoreCase) => $"drop sequence if exists {qualifiedName}",
                    var t when t.Contains("Schema", StringComparison.OrdinalIgnoreCase) => $"drop schema if exists {qualifiedName}",
                    _ => throw new InvalidOperationException($"Unsupported memory object type: {obj.ObjectType}")
                };
                command.ExecuteNonQuery();
            }, token);
        }
        finally
        {
            _memoryConnectionLock.Release();
        }
    }

    public QualityReport AnalyzeQuality(DataTable table)
    {
        var rows = table.Rows.Count;
        var columns = new List<QualityColumn>();
        foreach (DataColumn column in table.Columns)
        {
            var values = table.AsEnumerable().Select(r => r[column]).ToList();
            var nulls = values.Count(v => v == DBNull.Value || string.IsNullOrWhiteSpace(v?.ToString()));
            var unique = values.Where(v => v != DBNull.Value).Select(v => v?.ToString() ?? "").Distinct().Count();
            columns.Add(new QualityColumn
            {
                Name = column.ColumnName,
                DataType = column.DataType.Name,
                NullCount = nulls,
                NullPercent = rows == 0 ? 0 : nulls * 100.0 / rows,
                UniqueCount = unique
            });
        }

        var outliers = CountNumericOutliers(table);
        var nullPenalty = columns.Sum(c => c.NullPercent) / Math.Max(1, columns.Count) * 0.7;
        var outlierPenalty = rows == 0 ? 0 : outliers * 100.0 / rows * 0.3;
        return new QualityReport
        {
            RowsAnalyzed = rows,
            NumericOutliers = outliers,
            QualityScore = Math.Round(Math.Max(0, 100 - nullPenalty - outlierPenalty), 1),
            Columns = columns
        };
    }

    private async Task CopyCsvToParquetAsync(string csvPath, string parquetPath, CancellationToken token)
    {
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var connection = NewOpenDuckConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"copy (select * from read_csv_auto('{EscapePath(csvPath)}', nullstr=['#N/A','#VALUE!','#REF!','#DIV/0!','NULL','null','N/A',''])) to '{EscapePath(parquetPath)}' (format parquet, compression zstd)";
            command.ExecuteNonQuery();
        }, token);
    }

    private static string PrepareStatementsForExport(DuckDBConnection connection, string sql, CancellationToken token)
    {
        var statements = SplitStatements(sql).ToList();
        if (statements.Count == 0) throw new InvalidOperationException("Enter a SQL statement.");
        for (var i = 0; i < statements.Count - 1; i++)
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = statements[i];
            command.ExecuteNonQuery();
        }

        return TrimTrailingSemicolon(statements[^1]);
    }

    private static void CopyQueryToFile(DuckDBConnection connection, string sql, string outputPath, string format)
    {
        using var command = connection.CreateCommand();
        command.CommandText = format.Equals("csv", StringComparison.OrdinalIgnoreCase)
            ? $"copy ({sql}) to '{EscapePath(outputPath)}' (header, delimiter ',')"
            : $"copy ({sql}) to '{EscapePath(outputPath)}' (format parquet, compression zstd)";
        command.ExecuteNonQuery();
    }

    private static void WriteQueryToExcel(DuckDBConnection connection, string sql, string outputPath, IProgress<string>? progress, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        using var archiveStream = File.Create(outputPath);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

        WriteZipText(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");
        WriteZipText(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        WriteZipText(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>");
        WriteZipText(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>");

        var sheetEntry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var sheetStream = sheetEntry.Open();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false };
        using var writer = XmlWriter.Create(sheetStream, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteStartElement("sheetData");
        WriteExcelRow(writer, 1, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));

        var rowNumber = 1;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            rowNumber++;
            if (rowNumber > 1_048_576)
            {
                throw new InvalidOperationException("Excel .xlsx supports a maximum of 1,048,576 rows per sheet. Export to CSV or Parquet for larger results.");
            }

            var values = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }
            WriteExcelRow(writer, rowNumber, values);
            if (rowNumber % 50_000 == 0) progress?.Report($"Exported {rowNumber - 1:N0} Excel rows...");
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        progress?.Report($"Exported {rowNumber - 1:N0} Excel rows.");
    }

    private static void WriteZipText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void WriteExcelRow(XmlWriter writer, int rowNumber, IEnumerable<string?> values)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        var columnNumber = 1;
        foreach (var value in values)
        {
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", ExcelCellReference(columnNumber, rowNumber));
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is");
            writer.WriteElementString("t", value ?? "");
            writer.WriteEndElement();
            writer.WriteEndElement();
            columnNumber++;
        }
        writer.WriteEndElement();
    }

    private static string ExcelCellReference(int columnNumber, int rowNumber)
    {
        var letters = "";
        while (columnNumber > 0)
        {
            columnNumber--;
            letters = (char)('A' + columnNumber % 26) + letters;
            columnNumber /= 26;
        }
        return letters + rowNumber.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<long> CountParquetRowsAsync(string parquetPath, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var connection = NewOpenDuckConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"select count(*) from read_parquet('{EscapePath(parquetPath)}')";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, token);
    }

    private void RegisterDatasetViews(DuckDBConnection connection)
    {
        foreach (var dataset in _workspace.SnapshotDatasets())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"create or replace view \"{Workspace.SafeName(dataset.Name)}\" as select * from read_parquet('{EscapePath(dataset.Path)}')";
            command.ExecuteNonQuery();
        }
    }

    private static DuckDBConnection NewOpenDuckConnection()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static long? TryCountObject(DuckDBConnection connection, string schema, string name)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"select count(*) from {QuoteQualifiedName(schema, name)}";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static void AddOptionalMetadataRows(
        DuckDBConnection connection,
        List<InMemoryObjectInfo> objects,
        string objectType,
        string sql,
        string nameColumn,
        string schemaColumn,
        string detailColumn,
        CancellationToken token)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var table = new DataTable();
            table.Load(reader);
            foreach (DataRow row in table.Rows)
            {
                token.ThrowIfCancellationRequested();
                var name = Convert.ToString(row[nameColumn], CultureInfo.InvariantCulture) ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var schema = table.Columns.Contains(schemaColumn)
                    ? Convert.ToString(row[schemaColumn], CultureInfo.InvariantCulture) ?? "main"
                    : "main";
                var rawDetail = table.Columns.Contains(detailColumn)
                    ? Convert.ToString(row[detailColumn], CultureInfo.InvariantCulture) ?? ""
                    : "";
                var detail = objectType == "Index" && !string.IsNullOrWhiteSpace(rawDetail)
                    ? $"on {rawDetail}"
                    : rawDetail;
                objects.Add(new InMemoryObjectInfo
                {
                    Schema = schema,
                    Name = name,
                    ObjectType = objectType,
                    Detail = detail
                });
            }
        }
        catch
        {
            // Older DuckDB builds may not expose every duckdb_* metadata table. Keep the explorer useful with the objects we can read.
        }
    }

    private static string? TryGetObjectDefinition(DuckDBConnection connection, InMemoryObjectInfo obj)
    {
        try
        {
            var sql = obj.ObjectType switch
            {
                var t when t.Contains("View", StringComparison.OrdinalIgnoreCase) =>
                    "select sql from duckdb_views() where view_name = ?",
                var t when t.Contains("Table", StringComparison.OrdinalIgnoreCase) =>
                    "select sql from duckdb_tables() where table_name = ?",
                var t when t.Contains("Index", StringComparison.OrdinalIgnoreCase) =>
                    "select sql from duckdb_indexes() where index_name = ?",
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(sql)) return null;
            using var command = connection.CreateCommand();
            command.CommandText = sql.Replace("?", $"'{EscapeSqlLiteral(obj.Name)}'");
            return command.ExecuteScalar()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static int ObjectSortOrder(string objectType)
    {
        if (objectType.Contains("Table", StringComparison.OrdinalIgnoreCase)) return 0;
        if (objectType.Contains("View", StringComparison.OrdinalIgnoreCase)) return 1;
        if (objectType.Contains("Macro", StringComparison.OrdinalIgnoreCase) || objectType.Contains("Function", StringComparison.OrdinalIgnoreCase)) return 2;
        if (objectType.Contains("Index", StringComparison.OrdinalIgnoreCase)) return 3;
        if (objectType.Contains("Sequence", StringComparison.OrdinalIgnoreCase)) return 4;
        if (objectType.Contains("Schema", StringComparison.OrdinalIgnoreCase)) return 5;
        return 99;
    }

    private static string QuoteQualifiedName(string schema, string name)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return QuoteIdentifier(name);
        }
        return $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static DbConnection OpenDatabaseConnection(SavedConnection connection, string password)
    {
        var cfg = connection.Config;
        return connection.Kind switch
        {
            "odbc" => new OdbcConnection(BuildOdbcConnectionString(cfg, password)),
            "sqlserver" => new SqlConnection(BuildSqlServerConnectionString(cfg, password)),
            "snowflake" => new SnowflakeDbConnection { ConnectionString = BuildSnowflakeConnectionString(cfg, password) },
            _ => throw new InvalidOperationException("C# version currently supports ODBC, SQL Server, and Snowflake. Use SAP HANA through an ODBC DSN.")
        };
    }

    public static async Task TestDatabaseConnectionAsync(string kind, Dictionary<string, string> config, string password, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var isSnowflake = kind.Equals("snowflake", StringComparison.OrdinalIgnoreCase);
        var timeoutSeconds = isSnowflake ? 120 : 20;
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await using var connection = OpenDatabaseConnection(new SavedConnection { Kind = kind, Config = config }, password);
            if (isSnowflake)
            {
                await connection.OpenAsync(CancellationToken.None).WaitAsync(timeout.Token);
                await ExecuteConnectionTestQueryAsync(connection, kind, CancellationToken.None).WaitAsync(timeout.Token);
            }
            else
            {
                await connection.OpenAsync(timeout.Token);
                await ExecuteConnectionTestQueryAsync(connection, kind, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var accountHint = isSnowflake
                ? $" Account used: {config.GetValueOrDefault("account", "")}. A timeout usually means QuerySpace could not reach Snowflake through DNS/network/VPN/proxy before authentication completed."
                : "";
            throw new TimeoutException($"{kind} connection test did not complete within {timeoutSeconds} seconds.{accountHint} Check the server/account name, network/VPN/proxy, authentication method, and credentials.");
        }
    }

    private static async Task ExecuteConnectionTestQueryAsync(DbConnection connection, string kind, CancellationToken token)
    {
        try
        {
            await ExecuteScalarTestQueryAsync(connection, "select 1", token);
        }
        catch (OdbcException ex) when (kind.Equals("odbc", StringComparison.OrdinalIgnoreCase) && IsHanaOdbcSyntaxError(ex))
        {
            await ExecuteScalarTestQueryAsync(connection, "select 1 from dummy", token);
        }
    }

    private static async Task ExecuteScalarTestQueryAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        _ = await command.ExecuteScalarAsync(token);
    }

    private static bool IsHanaOdbcSyntaxError(OdbcException ex)
    {
        if (ex.Errors.Cast<OdbcError>().Any(error => error.NativeError == 257)) return true;
        return ex.Message.Contains("syntax error", StringComparison.OrdinalIgnoreCase) &&
               ex.Message.Contains("257", StringComparison.OrdinalIgnoreCase);
    }

    public const string SqlServerCertificateHelp =
        "SQL Server connection reached the server, but TLS certificate validation failed.\n\n" +
        "For local SQL Server Express or development SQL Server instances that use a self-signed/untrusted certificate, " +
        "enable \"Trust server certificate\" in the SQL Server connection settings and save the connection.\n\n" +
        "For production servers, install/use a certificate that chains to a trusted company root certificate instead of trusting the server certificate.";

    public static string SqlServerConnectionDiagnostic(Dictionary<string, string> config)
    {
        return
            "Current SQL Server TLS settings used by QuerySpace:\n" +
            $"- Encrypt: {config.GetValueOrDefault("encrypt", "true")}\n" +
            $"- Trust server certificate: {config.GetValueOrDefault("trustServerCertificate", "false")}\n" +
            $"- Server: {config.GetValueOrDefault("server", "")}\n" +
            $"- Database: {config.GetValueOrDefault("database", "")}";
    }

    public static string SnowflakeConnectionDiagnostic(Dictionary<string, string> config)
    {
        var authType = config.GetValueOrDefault("authType", "password");
        var authDescription = SnowflakeAuthDescription(authType);
        return
            "Current Snowflake settings used by QuerySpace:\n" +
            $"- Account: {config.GetValueOrDefault("account", "")}\n" +
            $"- User: {config.GetValueOrDefault("user", "")}\n" +
            $"- Auth method: {authDescription}\n" +
            $"- Warehouse: {config.GetValueOrDefault("warehouse", "")}\n" +
            $"- Database: {config.GetValueOrDefault("database", "")}\n" +
            $"- Schema: {config.GetValueOrDefault("schema", "")}\n" +
            $"- Role: {config.GetValueOrDefault("role", "")}\n\n" +
            "Sanitized connection string shape:\n" +
            BuildSnowflakeConnectionString(config, "<hidden>") + "\n\n" +
            "If you copied Snowflake's .NET Password connection string, choose Auth method = Password and paste the real password in Password / PAT / token.\n" +
            "If you are using a Snowflake Programmatic Access Token, choose Auth method = Programmatic Access Token (PAT) and paste the PAT value in Password / PAT / token.\n" +
            "For PAT, the token must belong to the same Snowflake user shown above and must not be expired or revoked.\n" +
            "If testing times out, first try with Warehouse, Database, Schema, and Role blank so only Account + User + PAT are tested.";
    }

    private static string SnowflakeAuthDescription(string authType)
    {
        if (authType.Equals("pat", StringComparison.OrdinalIgnoreCase)) return "Programmatic Access Token (PAT)";
        if (authType.Equals("oauth", StringComparison.OrdinalIgnoreCase)) return "OAuth access token";
        return "Password";
    }


    public static bool IsSqlServerUntrustedCertificateError(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("certificate chain was issued by an authority that is not trusted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("SSL Provider", StringComparison.OrdinalIgnoreCase) && message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string BuildOdbcConnectionString(Dictionary<string, string> cfg, string password)
    {
        var builder = new OdbcConnectionStringBuilder
        {
            ["DSN"] = cfg.GetValueOrDefault("dsn", "")
        };
        if (cfg.TryGetValue("database", out var db) && !string.IsNullOrWhiteSpace(db)) builder["Database"] = db;
        if (cfg.GetValueOrDefault("trusted") == "true") builder["Trusted_Connection"] = "yes";
        else
        {
            builder["UID"] = cfg.GetValueOrDefault("user", "");
            builder["PWD"] = password;
        }
        return builder.ConnectionString;
    }

    private static string BuildSqlServerConnectionString(Dictionary<string, string> cfg, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = cfg.GetValueOrDefault("server", ""),
            InitialCatalog = cfg.GetValueOrDefault("database", ""),
            TrustServerCertificate = cfg.GetValueOrDefault("trustServerCertificate") == "true",
            Encrypt = cfg.GetValueOrDefault("encrypt", "true") != "false",
            ConnectTimeout = 30
        };
        if (cfg.GetValueOrDefault("trusted") == "true") builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = cfg.GetValueOrDefault("user", "");
            builder.Password = password;
        }
        return builder.ConnectionString;
    }

    private static string BuildSnowflakeConnectionString(Dictionary<string, string> cfg, string token)
    {
        var authType = cfg.GetValueOrDefault("authType", "password");
        var builder = new DbConnectionStringBuilder
        {
            ["account"] = cfg.GetValueOrDefault("account", ""),
            ["user"] = cfg.GetValueOrDefault("user", "")
        };
        AddConnectionStringValue(builder, "warehouse", cfg.GetValueOrDefault("warehouse", ""));
        AddConnectionStringValue(builder, "db", cfg.GetValueOrDefault("database", ""));
        AddConnectionStringValue(builder, "schema", cfg.GetValueOrDefault("schema", ""));
        AddConnectionStringValue(builder, "role", cfg.GetValueOrDefault("role", ""));

        if (authType.Equals("oauth", StringComparison.OrdinalIgnoreCase))
        {
            builder["authenticator"] = "oauth";
            builder["token"] = token;
        }
        else if (authType.Equals("pat", StringComparison.OrdinalIgnoreCase))
        {
            builder["authenticator"] = "programmatic_access_token";
            builder["token"] = token;
        }
        else
        {
            builder["password"] = token;
        }

        return builder.ConnectionString;
    }

    private static void AddConnectionStringValue(DbConnectionStringBuilder builder, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder[key] = value;
        }
    }

    private static async Task<long> WriteReaderToParquetAsync(DbDataReader reader, string path, int batchSize, long? expectedRows, IProgress<ExtractProgress>? progress, CancellationToken token)
    {
        if (reader.FieldCount == 0) throw new InvalidOperationException("The base query returned no columns.");
        var columnNames = UniqueColumnNames(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => ParquetColumnBuffer.Create(columnNames[i], reader.GetFieldType(i)))
            .ToList();
        var schema = new ParquetSchema(columns.Select(c => c.Field));
        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(
            schema,
            stream,
            new ParquetOptions { CompressionMethod = CompressionMethod.Zstd },
            cancellationToken: token);
        long rows = 0;
        var batchRows = 0;
        var progressInterval = Math.Max(50_000, Math.Min(batchSize, 250_000));
        var nextProgressRows = progressInterval;
        while (await reader.ReadAsync(token))
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns[i].Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            rows++;
            batchRows++;
            if (rows >= nextProgressRows)
            {
                progress?.Report(new ExtractProgress { Message = $"Extracted {rows:N0} rows...", Rows = rows, ExpectedRows = expectedRows });
                nextProgressRows += progressInterval;
            }
            if (batchRows >= batchSize)
            {
                await WriteParquetRowGroupAsync(writer, columns, token);
                batchRows = 0;
                progress?.Report(new ExtractProgress { Message = $"Extracted {rows:N0} rows...", Rows = rows, ExpectedRows = expectedRows });
            }
        }
        if (batchRows > 0)
        {
            await WriteParquetRowGroupAsync(writer, columns, token);
        }
        progress?.Report(new ExtractProgress { Message = $"Extracted {rows:N0} rows.", Rows = rows, ExpectedRows = expectedRows });
        return rows;
    }

    private static async Task WriteParquetRowGroupAsync(ParquetWriter writer, IReadOnlyList<ParquetColumnBuffer> columns, CancellationToken token)
    {
        using var rowGroup = writer.CreateRowGroup();
        foreach (var column in columns)
        {
            token.ThrowIfCancellationRequested();
            await column.WriteAsync(rowGroup, token);
            column.Clear();
        }
    }

    private static void WriteFirstExcelSheetToCsv(string excelPath, string csvPath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = File.Open(excelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(false));
        do
        {
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var raw = reader.GetValue(i)?.ToString() ?? "";
                    values[i] = EscapeCsv(IsNullMarker(raw) ? "" : raw);
                }
                writer.WriteLine(string.Join(",", values));
            }
            break;
        } while (reader.NextResult());
    }

    private static int CountNumericOutliers(DataTable table)
    {
        var count = 0;
        foreach (DataColumn column in table.Columns)
        {
            var values = table.AsEnumerable()
                .Select(r => double.TryParse(r[column]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
                .Where(d => !double.IsNaN(d))
                .ToList();
            if (values.Count < 8) continue;
            var avg = values.Average();
            var std = Math.Sqrt(values.Sum(v => Math.Pow(v - avg, 2)) / values.Count);
            if (std <= 0) continue;
            count += values.Count(v => Math.Abs((v - avg) / std) > 3.0);
        }
        return count;
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var current = new StringBuilder();
        var inSingle = false;
        foreach (var ch in sql)
        {
            if (ch == '\'') inSingle = !inSingle;
            if (ch == ';' && !inSingle)
            {
                var statement = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(statement)) yield return statement;
                current.Clear();
            }
            else current.Append(ch);
        }
        var last = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(last)) yield return last;
    }

    private static bool IsReadOnly(string statement)
    {
        var s = statement.TrimStart().ToLowerInvariant();
        return s.StartsWith("select ") || s.StartsWith("with ") || s.StartsWith("describe ") || s.StartsWith("show ");
    }

    private static string TrimTrailingSemicolon(string sql) => sql.Trim().TrimEnd(';');
    private static string EscapePath(string path) => path.Replace("\\", "\\\\").Replace("'", "''");
    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");
    private static string EscapeCsv(string value) => value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
    private static bool IsNullMarker(string value)
    {
        var v = value.Trim();
        return v is "" or "#N/A" or "#VALUE!" or "#REF!" or "#DIV/0!" or "NULL" or "null" or "N/A";
    }

    private static List<string> UniqueColumnNames(IEnumerable<string> names)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var baseName = string.IsNullOrWhiteSpace(raw) ? "column" : raw.Trim();
            if (!used.TryGetValue(baseName, out var count))
            {
                used[baseName] = 1;
                result.Add(baseName);
                continue;
            }
            count++;
            used[baseName] = count;
            result.Add($"{baseName}_{count}");
        }
        return result;
    }

    private abstract class ParquetColumnBuffer
    {
        protected ParquetColumnBuffer(DataField field) => Field = field;
        public DataField Field { get; }
        public abstract void Add(object? value);
        public abstract Task WriteAsync(ParquetRowGroupWriter writer, CancellationToken token);
        public abstract void Clear();

        public static ParquetColumnBuffer Create(string name, Type providerType)
        {
            var type = Nullable.GetUnderlyingType(providerType) ?? providerType;
            if (type == typeof(bool)) return new TypedParquetColumnBuffer<bool>(name, Convert.ToBoolean);
            if (type == typeof(byte)) return new TypedParquetColumnBuffer<int>(name, value => Convert.ToInt32(value, CultureInfo.InvariantCulture));
            if (type == typeof(short)) return new TypedParquetColumnBuffer<int>(name, value => Convert.ToInt32(value, CultureInfo.InvariantCulture));
            if (type == typeof(int)) return new TypedParquetColumnBuffer<int>(name, value => Convert.ToInt32(value, CultureInfo.InvariantCulture));
            if (type == typeof(long)) return new TypedParquetColumnBuffer<long>(name, value => Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (type == typeof(float)) return new TypedParquetColumnBuffer<float>(name, value => Convert.ToSingle(value, CultureInfo.InvariantCulture));
            if (type == typeof(double)) return new TypedParquetColumnBuffer<double>(name, value => Convert.ToDouble(value, CultureInfo.InvariantCulture));
            if (type == typeof(decimal)) return new TypedParquetColumnBuffer<decimal>(name, value => Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            if (type == typeof(DateTime)) return new TypedParquetColumnBuffer<DateTime>(name, value => Convert.ToDateTime(value, CultureInfo.InvariantCulture));
            if (type == typeof(byte[])) return new BytesParquetColumnBuffer(name);
            return new StringParquetColumnBuffer(name);
        }
    }

    private sealed class TypedParquetColumnBuffer<T> : ParquetColumnBuffer where T : struct
    {
        private readonly Func<object, T> _convert;
        private readonly List<T?> _values = new();

        public TypedParquetColumnBuffer(string name, Func<object, T> convert)
            : base(new DataField(name, typeof(T), isNullable: true))
        {
            _convert = convert;
        }

        public override void Add(object? value)
        {
            _values.Add(value == null ? null : _convert(value));
        }

        public override async Task WriteAsync(ParquetRowGroupWriter writer, CancellationToken token)
        {
            await writer.WriteAsync<T>(Field, _values.ToArray(), cancellationToken: token);
        }

        public override void Clear() => _values.Clear();
    }

    private sealed class StringParquetColumnBuffer : ParquetColumnBuffer
    {
        private readonly List<string?> _values = new();

        public StringParquetColumnBuffer(string name)
            : base(new DataField(name, typeof(string), isNullable: true))
        {
        }

        public override void Add(object? value)
        {
            var text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            _values.Add(text != null && IsNullMarker(text) ? null : text);
        }

        public override async Task WriteAsync(ParquetRowGroupWriter writer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteAsync(Field, _values);
        }

        public override void Clear() => _values.Clear();
    }

    private sealed class BytesParquetColumnBuffer : ParquetColumnBuffer
    {
        private readonly List<byte[]?> _values = new();

        public BytesParquetColumnBuffer(string name)
            : base(new DataField(name, typeof(byte[]), isNullable: true))
        {
        }

        public override void Add(object? value)
        {
            _values.Add(value as byte[]);
        }

        public override async Task WriteAsync(ParquetRowGroupWriter writer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteAsync(Field, _values);
        }

        public override void Clear() => _values.Clear();
    }
}
