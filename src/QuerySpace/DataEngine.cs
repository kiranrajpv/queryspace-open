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
using System.Text;

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
                    if (i == statements.Count - 1) throw;
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

    public async Task ExportCsvAsync(string sql, string outputPath, CancellationToken token)
    {
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var connection = NewOpenDuckConnection();
            RegisterDatasetViews(connection);
            var last = SplitStatements(sql).LastOrDefault() ?? throw new InvalidOperationException("Enter a SQL statement.");
            using var command = connection.CreateCommand();
            command.CommandText = $"copy ({TrimTrailingSemicolon(last)}) to '{EscapePath(outputPath)}' (header, delimiter ',')";
            command.ExecuteNonQuery();
        }, token);
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
            _workspace.RecordExtractQuery(request.Connection.Id, null, name, request.Sql, null, "cancelled");
            throw;
        }
        catch (Exception ex) when (IsSqlServerUntrustedCertificateError(ex))
        {
            _workspace.RecordExtractQuery(request.Connection.Id, null, name, request.Sql, null, "failed");
            throw new InvalidOperationException(SqlServerCertificateHelp + "\n\n" + SqlServerConnectionDiagnostic(request.Connection.Config), ex);
        }
        catch
        {
            _workspace.RecordExtractQuery(request.Connection.Id, null, name, request.Sql, null, "failed");
            throw;
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

    public async Task<MaterialCompareResult> CompareMandatoryMaterialsAsync(MaterialCompareRequest request, CancellationToken token)
    {
        if (request.KeyMappings.Count == 0) throw new InvalidOperationException("Select at least one key column mapping.");
        if (request.KeyMappings.Any(k => string.IsNullOrWhiteSpace(k.ReferenceColumn) || string.IsNullOrWhiteSpace(k.TargetColumn)))
        {
            throw new InvalidOperationException("Every key mapping must have both a reference and target column.");
        }

        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var connection = NewOpenDuckConnection();
            RegisterDatasetViews(connection);

            var referenceView = QuoteIdentifier(Workspace.SafeName(request.ReferenceDataset.Name));
            var targetView = QuoteIdentifier(Workspace.SafeName(request.TargetDataset.Name));
            var joinPredicate = string.Join(" and ", request.KeyMappings.Select(k =>
                $"{NormalizeSql("r", k.ReferenceColumn, request.NormalizeKeys)} = {NormalizeSql("t", k.TargetColumn, request.NormalizeKeys)}"));
            var missingSql = $"select r.* from {referenceView} r where not exists (select 1 from {targetView} t where {joinPredicate})";
            var allReferenceWithFlagSql = $"select r.*, case when exists (select 1 from {targetView} t where {joinPredicate}) then 0 else 1 end as __queryspace_missing from {referenceView} r";

            var referenceRows = ExecuteScalarLong(connection, $"select count(*) from {referenceView}");
            var missingRowsCount = ExecuteScalarLong(connection, $"select count(*) from ({missingSql}) missing");
            var foundRows = Math.Max(0, referenceRows - missingRowsCount);
            var missingTable = ExecuteTable(connection, $"select * from ({missingSql}) missing limit {Math.Max(1, request.PreviewRows)}");
            var coverage = BuildCoverageTable(referenceRows, foundRows, missingRowsCount);
            var columnImpact = BuildColumnImpactTable(connection, allReferenceWithFlagSql, request.ExplainColumns, missingRowsCount, token);
            var reasonSummary = BuildReasonSummaryTable(columnImpact, request, missingRowsCount, referenceRows);

            DatasetInfo? savedDataset = null;
            if (request.SaveMissingAsDataset)
            {
                var outputName = _workspace.EnsureDatasetNameAvailable(
                    string.IsNullOrWhiteSpace(request.OutputDatasetName)
                        ? $"missing_{request.ReferenceDataset.Name}_vs_{request.TargetDataset.Name}"
                        : request.OutputDatasetName);
                var finalPath = Path.Combine(_workspace.DatasetsFolder, outputName + ".parquet");
                var partialPath = finalPath + ".partial";
                if (File.Exists(finalPath)) throw new InvalidOperationException($"Parquet file already exists: {finalPath}");
                if (File.Exists(partialPath)) File.Delete(partialPath);
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"copy ({missingSql}) to '{EscapePath(partialPath)}' (format parquet, compression zstd)";
                    command.ExecuteNonQuery();
                    File.Move(partialPath, finalPath);
                    savedDataset = _workspace.AddDataset(outputName, finalPath, null, missingRowsCount);
                }
                catch
                {
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                    throw;
                }
            }

            return new MaterialCompareResult
            {
                MissingRows = missingTable,
                Coverage = coverage,
                ReasonSummary = reasonSummary,
                ColumnImpact = columnImpact,
                SqlUsed = missingSql + ";",
                SavedDataset = savedDataset,
                ReferenceRows = referenceRows,
                FoundRows = foundRows,
                MissingRowsCount = missingRowsCount
            };
        }, token);
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
        await using var connection = OpenDatabaseConnection(new SavedConnection { Kind = kind, Config = config }, password);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select 1";
        command.CommandTimeout = 15;
        _ = await command.ExecuteScalarAsync(token);
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
        var parts = new[]
        {
            $"account={cfg.GetValueOrDefault("account", "")}",
            $"user={cfg.GetValueOrDefault("user", "")}",
            $"authenticator=oauth",
            $"token={token}",
            $"warehouse={cfg.GetValueOrDefault("warehouse", "")}",
            $"db={cfg.GetValueOrDefault("database", "")}",
            $"schema={cfg.GetValueOrDefault("schema", "")}",
            $"role={cfg.GetValueOrDefault("role", "")}"
        };
        return string.Join(';', parts.Where(p => !p.EndsWith("=", StringComparison.Ordinal)));
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

    private static DataTable BuildCoverageTable(long referenceRows, long foundRows, long missingRows)
    {
        var table = new DataTable();
        table.Columns.Add("Metric", typeof(string));
        table.Columns.Add("Value", typeof(string));
        table.Rows.Add("Reference required rows", referenceRows.ToString("N0", CultureInfo.InvariantCulture));
        table.Rows.Add("Found in target", foundRows.ToString("N0", CultureInfo.InvariantCulture));
        table.Rows.Add("Missing from target", missingRows.ToString("N0", CultureInfo.InvariantCulture));
        var coverage = referenceRows == 0 ? 100 : foundRows * 100.0 / referenceRows;
        table.Rows.Add("Coverage", coverage.ToString("N2", CultureInfo.InvariantCulture) + "%");
        return table;
    }

    private static DataTable BuildColumnImpactTable(DuckDBConnection connection, string allReferenceWithFlagSql, IReadOnlyList<string> explainColumns, long totalMissing, CancellationToken token)
    {
        var table = new DataTable();
        table.Columns.Add("Column", typeof(string));
        table.Columns.Add("Top value among missing", typeof(string));
        table.Columns.Add("Missing rows", typeof(long));
        table.Columns.Add("Missing share", typeof(string));
        table.Columns.Add("Value missing rate", typeof(string));
        table.Columns.Add("Impact", typeof(string));
        table.Columns.Add("Reason signal", typeof(string));

        if (totalMissing <= 0) return table;

        foreach (var column in explainColumns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var col = QuoteIdentifier(column);
            var sql =
                "with flagged as (" + allReferenceWithFlagSql + "), " +
                $"grouped as (select coalesce(cast({col} as varchar), '<NULL>') as value, count(*) as total_rows, sum(__queryspace_missing) as missing_rows from flagged group by 1) " +
                "select value, total_rows, missing_rows, missing_rows * 100.0 / nullif(total_rows, 0) as value_missing_rate, missing_rows * 100.0 / nullif((select sum(missing_rows) from grouped), 0) as missing_share " +
                "from grouped where missing_rows > 0 order by missing_rows desc, value_missing_rate desc limit 1";
            var grouped = ExecuteTable(connection, sql);
            if (grouped.Rows.Count == 0) continue;
            var row = grouped.Rows[0];
            var value = row["value"]?.ToString() ?? "";
            var missingRows = Convert.ToInt64(row["missing_rows"], CultureInfo.InvariantCulture);
            var valueMissingRate = Convert.ToDouble(row["value_missing_rate"], CultureInfo.InvariantCulture);
            var missingShare = Convert.ToDouble(row["missing_share"], CultureInfo.InvariantCulture);
            var impactScore = Math.Min(100, missingShare * 0.65 + valueMissingRate * 0.35);
            var impact = impactScore >= 60 ? "High" : impactScore >= 30 ? "Medium" : "Low";
            var reason = $"{missingShare:N1}% of missing rows have {column} = {value}; {valueMissingRate:N1}% of reference rows with this value are missing.";
            table.Rows.Add(column, value, missingRows, missingShare.ToString("N1", CultureInfo.InvariantCulture) + "%", valueMissingRate.ToString("N1", CultureInfo.InvariantCulture) + "%", impact, reason);
        }

        var view = table.DefaultView;
        view.Sort = "Missing rows DESC";
        return view.ToTable();
    }

    private static DataTable BuildReasonSummaryTable(DataTable columnImpact, MaterialCompareRequest request, long missingRows, long referenceRows)
    {
        var table = new DataTable();
        table.Columns.Add("Rank", typeof(int));
        table.Columns.Add("Possible reason", typeof(string));
        table.Columns.Add("Evidence", typeof(string));

        if (missingRows == 0)
        {
            table.Rows.Add(1, "No mandatory materials are missing.", "Every reference row matched the target dataset using the selected key mapping.");
            return table;
        }

        table.Rows.Add(1, "Target does not contain every mandatory reference key.", $"{missingRows:N0} of {referenceRows:N0} reference rows are missing from target {request.TargetDataset.Name}.");
        var rank = 2;
        foreach (DataRow impact in columnImpact.Rows.Cast<DataRow>().Take(5))
        {
            table.Rows.Add(rank++, $"Missing rows are concentrated around {impact["Column"]} = {impact["Top value among missing"]}.", impact["Reason signal"]?.ToString() ?? "");
        }

        if (request.ContextDataset != null)
        {
            table.Rows.Add(rank, "A context/previous dataset was selected for investigation.", $"Use {request.ContextDataset.Name} to check whether these materials existed in a prior extract or source snapshot.");
        }

        return table;
    }

    private static DataTable ExecuteTable(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    private static long ExecuteScalarLong(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static string NormalizeSql(string alias, string column, bool normalize)
    {
        var expr = $"{alias}.{QuoteIdentifier(column)}";
        if (!normalize) return expr;
        return $"upper(ltrim(trim(cast({expr} as varchar)), '0'))";
    }

    private static string EscapePath(string path) => path.Replace("\\", "\\\\").Replace("'", "''");
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
