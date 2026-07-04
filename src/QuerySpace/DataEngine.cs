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

        var connection = mode.Equals("Memory", StringComparison.OrdinalIgnoreCase)
            ? _memoryConnection
            : NewOpenDuckConnection();
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

    public async Task<DatasetInfo> ExtractAsync(ExtractRequest request, IProgress<string>? progress, CancellationToken token)
    {
        var name = _workspace.EnsureDatasetNameAvailable(request.DatasetName);
        var finalPath = Path.Combine(_workspace.DatasetsFolder, name + ".parquet");
        var partialParquet = finalPath + ".partial";
        if (File.Exists(finalPath)) throw new InvalidOperationException($"Parquet file already exists: {finalPath}");
        if (File.Exists(partialParquet)) File.Delete(partialParquet);

        try
        {
            progress?.Report($"Connecting to {request.Connection.Kind}...");
            await using var connection = OpenDatabaseConnection(request.Connection, request.Password);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = request.Sql;
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            var rows = await WriteReaderToParquetAsync(reader, partialParquet, request.BatchSize, progress, token);
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

    private async Task CopyCsvToParquetAsync(string csvPath, string parquetPath, CancellationToken token)
    {
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var connection = NewOpenDuckConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"copy (select * from read_csv_auto('{EscapePath(csvPath)}', nullstr ['#N/A','#VALUE!','#REF!','#DIV/0!','NULL','null','N/A',''])) to '{EscapePath(parquetPath)}' (format parquet, compression zstd)";
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
        foreach (var dataset in _workspace.State.Datasets)
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
        var parts = new List<string> { $"DSN={cfg.GetValueOrDefault("dsn", "")}" };
        if (cfg.TryGetValue("database", out var db) && !string.IsNullOrWhiteSpace(db)) parts.Add($"Database={db}");
        if (cfg.GetValueOrDefault("trusted") == "true") parts.Add("Trusted_Connection=yes");
        else
        {
            parts.Add($"UID={cfg.GetValueOrDefault("user", "")}");
            parts.Add($"PWD={password}");
        }
        return string.Join(';', parts);
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

    private static async Task<long> WriteReaderToParquetAsync(DbDataReader reader, string path, int batchSize, IProgress<string>? progress, CancellationToken token)
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
        while (await reader.ReadAsync(token))
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns[i].Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            rows++;
            batchRows++;
            if (batchRows >= batchSize)
            {
                await WriteParquetRowGroupAsync(writer, columns, token);
                batchRows = 0;
                progress?.Report($"Extracted {rows:N0} rows...");
            }
        }
        if (batchRows > 0)
        {
            await WriteParquetRowGroupAsync(writer, columns, token);
        }
        progress?.Report($"Extracted {rows:N0} rows.");
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
