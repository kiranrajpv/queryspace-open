using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.Win32;
using System.Diagnostics;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuerySpace;

public partial class MainWindow : Window
{
    private Workspace _workspace = new();
    private DataEngine _engine;
    private CancellationTokenSource? _extractCts;
    private DataTable? _currentResults;
    private const int MaxScriptExecutions = 3;
    private readonly Dictionary<string, ScriptExecutionState> _scriptExecutionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _scriptExecutionQueue = new();
    private readonly List<InMemoryObjectInfo> _memoryCompletionObjects = new();
    private CompletionWindow? _completionWindow;
    private int _activeScriptExecutions;

    public MainWindow()
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        _engine = new DataEngine(_workspace);
        InitializeSqlEditor();
        RefreshExplorer();
        _ = RefreshInMemoryExplorerAsync();
        SetStatus("Ready");
    }

    private void InitializeSqlEditor()
    {
        SqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");
        SqlEditor.TextArea.TextView.LineTransformers.Add(new DuckDbSqlColorizer());
        SqlEditor.Options.EnableHyperlinks = false;
        SqlEditor.Options.EnableEmailHyperlinks = false;
        SqlEditor.Options.ConvertTabsToSpaces = true;
        SqlEditor.Options.IndentationSize = 4;
        SqlEditor.TextArea.TextEntered += SqlEditor_TextEntered;
        SqlEditor.TextArea.TextEntering += SqlEditor_TextEntering;
        SqlEditor.PreviewKeyDown += SqlEditor_PreviewKeyDown;
    }

    private void SqlEditor_TextEntered(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];
        if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
        {
            ShowSqlCompletion(false);
        }
    }

    private void SqlEditor_TextEntering(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0 && _completionWindow != null)
        {
            var ch = e.Text[0];
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '.')
            {
                _completionWindow.CompletionList.RequestInsertion(e);
            }
        }
    }

    private void SqlEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowSqlCompletion(true);
            e.Handled = true;
        }
    }

    private void ShowSqlCompletion(bool force)
    {
        var segment = CurrentCompletionSegment();
        var prefix = SqlEditor.Document.GetText(segment.StartOffset, segment.Length).TrimStart('"', '[', '`');
        var suggestions = BuildSqlCompletionSuggestions(prefix, force).ToList();
        if (suggestions.Count == 0)
        {
            _completionWindow?.Close();
            return;
        }

        _completionWindow?.Close();
        _completionWindow = new CompletionWindow(SqlEditor.TextArea)
        {
            StartOffset = segment.StartOffset,
            EndOffset = segment.EndOffset
        };
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var suggestion in suggestions)
        {
            data.Add(new SqlCompletionData(suggestion));
        }
        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    private TextSegment CurrentCompletionSegment()
    {
        var offset = SqlEditor.CaretOffset;
        var text = SqlEditor.Document.Text;
        var start = offset;
        while (start > 0)
        {
            var ch = text[start - 1];
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '.' && ch != '"' && ch != '[' && ch != ']' && ch != '`') break;
            start--;
        }
        return new TextSegment { StartOffset = start, Length = offset - start };
    }

    private IEnumerable<SqlSuggestion> BuildSqlCompletionSuggestions(string prefix, bool force)
    {
        var normalizedPrefix = prefix.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suggestion in StaticSqlSuggestions())
        {
            if (MatchesCompletionPrefix(suggestion.Text, normalizedPrefix, force) && seen.Add(suggestion.Text)) yield return suggestion;
        }

        foreach (var dataset in _workspace.SnapshotDatasets().OrderBy(d => d.Name))
        {
            var name = Workspace.SafeName(dataset.Name);
            if (MatchesCompletionPrefix(name, normalizedPrefix, force) && seen.Add(name))
            {
                yield return new SqlSuggestion(name, name, $"Local dataset ({dataset.RowCount:N0} rows)", 1.0);
            }
        }

        foreach (var obj in _memoryCompletionObjects.OrderBy(o => o.ObjectType).ThenBy(o => o.Name))
        {
            var name = obj.Name;
            if (MatchesCompletionPrefix(name, normalizedPrefix, force) && seen.Add(name))
            {
                yield return new SqlSuggestion(name, name, $"In-Memory {obj.ObjectType}" + (string.IsNullOrWhiteSpace(obj.Detail) ? "" : $" {obj.Detail}"), 0.95);
            }
        }
    }

    private static bool MatchesCompletionPrefix(string text, string prefix, bool force)
    {
        if (force || string.IsNullOrWhiteSpace(prefix)) return true;
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
               text.Contains(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SqlSuggestion> StaticSqlSuggestions()
    {
        SqlSuggestion[] snippets =
        [
            new("create table", "create table table_name as\nselect * from source_name;\n", "DuckDB snippet: create table from query", 1.2, "table_name"),
            new("create temp table", "create temp table table_name as\nselect * from source_name;\n", "DuckDB snippet: create temporary table", 1.19, "table_name"),
            new("create view", "create or replace view view_name as\nselect * from source_name;\n", "DuckDB snippet: create or replace view", 1.2, "view_name"),
            new("create temp view", "create or replace temp view view_name as\nselect * from source_name;\n", "DuckDB snippet: create temporary view", 1.19, "view_name"),
            new("create macro", "create macro macro_name(x) as\ncase\n    when x is null then null\n    else x\nend;\n", "DuckDB snippet: reusable SQL macro/function", 1.18, "macro_name"),
            new("create index", "create index index_name on table_name(column_name);\n", "DuckDB snippet: create index", 1.17, "index_name"),
            new("create sequence", "create sequence sequence_name start 1 increment 1;\n", "DuckDB snippet: create sequence", 1.16, "sequence_name"),
            new("create schema", "create schema schema_name;\n", "DuckDB snippet: create schema", 1.15, "schema_name"),
            new("copy to parquet", "copy (\n    select * from source_name\n) to 'output.parquet' (format parquet, compression zstd);\n", "DuckDB snippet: export query to Parquet", 1.14, "source_name")
        ];
        foreach (var snippet in snippets)
        {
            yield return snippet;
        }

        string[] keywords =
        [
            "select", "from", "where", "join", "left join", "inner join", "group by", "order by", "having",
            "limit", "drop table", "drop view", "drop macro",
            "insert into", "copy", "describe", "show tables", "with", "as", "case", "when", "then", "else", "end",
            "and", "or", "not", "is null", "is not null", "union all"
        ];
        foreach (var keyword in keywords)
        {
            yield return new SqlSuggestion(keyword, keyword, "SQL keyword", 0.8);
        }

        string[] functions =
        [
            "count()", "sum()", "avg()", "min()", "max()", "coalesce()", "nullif()", "upper()", "lower()", "trim()",
            "regexp_replace()", "regexp_matches()", "strptime()", "strftime()", "cast()", "try_cast()", "round()",
            "abs()", "length()", "substring()", "replace()", "split_part()", "date_diff()", "date_trunc()",
            "row_number() over ()", "rank() over ()", "dense_rank() over ()"
        ];
        foreach (var function in functions)
        {
            yield return new SqlSuggestion(function, function, "DuckDB function", 0.7);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Data files|*.csv;*.xlsx;*.xls;*.parquet|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var name = Prompt("Dataset name", "Unique dataset name:", Path.GetFileNameWithoutExtension(dialog.FileName));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!TryEnsureDatasetNameAvailable(name, "Import dataset")) return;
        try
        {
            SetStatus("Importing...", true);
            var dataset = await _engine.ImportFileAsync(dialog.FileName, name, new Progress<string>(m => SetStatus(m, true)), CancellationToken.None);
            SetStatus($"Imported {dataset.Name}: {dataset.RowCount:N0} rows");
            RefreshExplorer();
        }
        catch (Exception ex)
        {
            SetStatus("Import failed");
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select QuerySpace workspace folder" };
        if (dialog.ShowDialog(this) != true) return;
        _workspace.SetRoot(dialog.FolderName);
        _engine = new DataEngine(_workspace);
        RefreshExplorer();
        SetStatus("Workspace changed");
    }

    private void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog(_workspace) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _workspace.AddConnection(dialog.ConnectionName, dialog.Kind, dialog.Config, dialog.Secret);
        RefreshExplorer();
        SetStatus($"Saved connection {dialog.ConnectionName}");
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_extractCts != null)
        {
            MessageBox.Show(this, "An extraction is already running. Stop it before starting another.", "Extract Base Query");
            return;
        }
        var connections = _workspace.SnapshotConnections();
        if (connections.Count == 0)
        {
            MessageBox.Show(this, "Add a database connection first.", "Extract Base Query");
            return;
        }
        var dialog = new ExtractDialog(connections, SqlEditor.Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunExtractionAsync(dialog.SelectedConnection, dialog.DatasetName, dialog.Sql, dialog.BatchSize, dialog.ExpectedRows);
    }

    private async Task RunExtractionAsync(SavedConnection connection, string datasetName, string sql, int batchSize, long? expectedRows)
    {
        if (!TryEnsureDatasetNameAvailable(datasetName, "Extract Base Query")) return;
        var password = _workspace.GetSecret(connection.Id);
        _extractCts = new CancellationTokenSource();
        StopExtractButton.Visibility = Visibility.Visible;
        StartExtractProgress(expectedRows);
        try
        {
            var request = new ExtractRequest
            {
                Connection = connection,
                Password = password,
                DatasetName = datasetName,
                Sql = sql,
                BatchSize = batchSize,
                ExpectedRows = expectedRows
            };
            SetStatus($"Starting extraction for {datasetName}...", true);
            var progress = new Progress<ExtractProgress>(UpdateExtractProgress);
            var dataset = await Task.Run(() => _engine.ExtractAsync(request, progress, _extractCts.Token), _extractCts.Token);
            SetStatus($"Extracted {dataset.Name}: {dataset.RowCount:N0} rows");
            RefreshExplorer();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Extraction stopped. Partial output was discarded.");
        }
        catch (Exception ex)
        {
            SetStatus("Extraction failed");
            MessageBox.Show(this, ex.Message, "Extraction failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _extractCts?.Dispose();
            _extractCts = null;
            StopExtractButton.Visibility = Visibility.Collapsed;
            StopExtractProgress();
        }
    }

    private bool TryEnsureDatasetNameAvailable(string datasetName, string title)
    {
        try
        {
            _workspace.EnsureDatasetNameAvailable(datasetName);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus("Dataset name already exists");
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void StartExtractProgress(long? expectedRows)
    {
        ExtractProgressBar.Visibility = Visibility.Visible;
        ExtractProgressBar.Value = 0;
        ExtractProgressBar.IsIndeterminate = !expectedRows.HasValue || expectedRows.Value <= 0;
    }

    private void StopExtractProgress()
    {
        ExtractProgressBar.IsIndeterminate = false;
        ExtractProgressBar.Value = 0;
        ExtractProgressBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateExtractProgress(ExtractProgress progress)
    {
        SetStatus(progress.Message, true);
        if (progress.ExpectedRows.HasValue && progress.ExpectedRows.Value > 0 && progress.Rows.HasValue)
        {
            ExtractProgressBar.IsIndeterminate = false;
            ExtractProgressBar.Value = Math.Min(100, progress.Rows.Value * 100.0 / progress.ExpectedRows.Value);
        }
    }

    private void StopExtraction_Click(object sender, RoutedEventArgs e)
    {
        StopExtractButton.IsEnabled = false;
        SetStatus("Stopping extraction and discarding partial output...", true);
        _extractCts?.Cancel();
    }

    private async void RunSql_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Running SQL...", true);
            var sql = SqlEditor.Text;
            var previewRows = int.TryParse(PreviewRowsText.Text, out var value) ? value : 1000;
            var mode = UsageCombo.SelectedItem is ComboBoxItem item
                ? item.Content?.ToString() ?? "Memory"
                : UsageCombo.SelectedItem?.ToString() ?? "Memory";
            var result = await Task.Run(() => _engine.ExecuteAsync(sql, previewRows, mode, CancellationToken.None));
            _currentResults = result.Table;
            ResultsGrid.ItemsSource = result.Table.DefaultView;
            if (mode.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshInMemoryExplorerAsync();
            }
            SetStatus(mode.Equals("Memory", StringComparison.OrdinalIgnoreCase)
                ? $"SQL completed. Displayed {result.DisplayedRows:N0} preview rows. In-Memory objects refreshed."
                : $"SQL completed. Displayed {result.DisplayedRows:N0} preview rows.");
        }
        catch (Exception ex)
        {
            SetStatus("SQL failed");
            MessageBox.Show(this, ex.Message, "SQL failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSql_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Save SQL", "Script name:", "query.sql");
        if (string.IsNullOrWhiteSpace(name)) return;
        _workspace.SaveScript(name, SqlEditor.Text);
        RefreshExplorer();
        SetStatus($"Saved SQL {name}");
    }

    private async void ExportResult_Click(object sender, RoutedEventArgs e)
    {
        var format = PromptExportFormat();
        if (!format.HasValue) return;
        var extension = format.Value switch
        {
            ExportFormat.Csv => ".csv",
            ExportFormat.Excel => ".xlsx",
            ExportFormat.Parquet => ".parquet",
            _ => ".dat"
        };
        var filter = format.Value switch
        {
            ExportFormat.Csv => "CSV files|*.csv",
            ExportFormat.Excel => "Excel files|*.xlsx",
            ExportFormat.Parquet => "Parquet files|*.parquet",
            _ => "All files|*.*"
        };
        var dialog = new SaveFileDialog { Filter = filter, InitialDirectory = _workspace.ExportsFolder, FileName = "query-results" + extension };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StartExportProgress();
            SetStatus($"Exporting {format.Value}...", true);
            await _engine.ExportResultAsync(SqlEditor.Text, dialog.FileName, format.Value, new Progress<string>(m => SetStatus(m, true)), CancellationToken.None);
            SetStatus($"Exported {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed");
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopExtractProgress();
        }
    }

    private void AnalyzeQuality_Click(object sender, RoutedEventArgs e)
    {
        if (_currentResults == null)
        {
            MessageBox.Show(this, "Run a query first.", "Analyze Quality");
            return;
        }
        var report = _engine.AnalyzeQuality(_currentResults);
        new QualityDialog(report) { Owner = this }.ShowDialog();
    }

    private void AnalyzeTransform_Click(object sender, RoutedEventArgs e)
    {
        if (_currentResults == null)
        {
            MessageBox.Show(this, "Run a tagged transformation SQL query first.", "Analyze Transform");
            return;
        }

        try
        {
            var report = TransformationAnalysisService.Analyze(SqlEditor.Text, _currentResults);
            new TransformationAnalysisDialog(report) { Owner = this }.ShowDialog();
            SetStatus($"Transformation analysis completed. Score {report.QualityScore:N1}.");
        }
        catch (Exception ex)
        {
            SetStatus("Transformation analysis failed");
            MessageBox.Show(this, ex.Message, "Analyze Transform", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DatasetsTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedTreeValue(DatasetsTree) is DatasetInfo dataset)
        {
            await ShowDatasetInfo(dataset);
        }
    }

    private async void DatasetInfo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTreeValue(DatasetsTree) is DatasetInfo dataset) await ShowDatasetInfo(dataset);
    }

    private void RenameDataset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTreeValue(DatasetsTree) is not DatasetInfo dataset) return;
        var name = Prompt("Rename dataset", "Dataset name:", dataset.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _workspace.RenameDataset(dataset, name);
            RefreshExplorer();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rename dataset");
        }
    }

    private void DeleteDataset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTreeValue(DatasetsTree) is not DatasetInfo dataset) return;
        if (MessageBox.Show(this, $"Delete dataset \"{dataset.Name}\" and its Parquet file?", "Delete dataset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _workspace.DeleteDataset(dataset);
        RefreshExplorer();
    }

    private void ScriptsTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedTreeValue(ScriptsTree) is string path && File.Exists(path))
        {
            SqlEditor.Text = File.ReadAllText(path);
            SetStatus($"Opened {Path.GetFileName(path)}");
        }
    }

    private void TemplatesTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedTreeValue(TemplatesTree) is string path && File.Exists(path))
        {
            SqlEditor.Text = File.ReadAllText(path);
            SetStatus($"Loaded template {Path.GetFileName(path)}");
        }
    }

    private async void MemoryTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedMemoryObject() is { } obj)
        {
            await PreviewMemoryObjectAsync(obj);
        }
    }

    private async void MemoryPreview_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryObject() is { } obj) await PreviewMemoryObjectAsync(obj);
    }

    private async void MemoryInfo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryObject() is not { } obj) return;
        try
        {
            var details = await _engine.GetInMemoryObjectDetailsAsync(obj, CancellationToken.None);
            ShowInMemoryObjectInfo(details);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "In-Memory object information", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MemoryGenerateSelect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryObject() is not { } obj) return;
        SqlEditor.Text = MemoryObjectSqlSnippet(obj);
        UsageCombo.SelectedIndex = 0;
        SetStatus(CanQueryMemoryObject(obj) ? $"Generated SELECT for {obj.Name}" : $"Generated SQL snippet for {obj.Name}");
    }

    private async void MemoryExport_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryObject() is not { } obj) return;
        if (!CanQueryMemoryObject(obj))
        {
            MessageBox.Show(this, "Only tables and views can be exported.", "Export In-Memory object");
            return;
        }
        var format = PromptExportFormat();
        if (!format.HasValue) return;
        var extension = format.Value switch
        {
            ExportFormat.Csv => ".csv",
            ExportFormat.Excel => ".xlsx",
            ExportFormat.Parquet => ".parquet",
            _ => ".dat"
        };
        var filter = format.Value switch
        {
            ExportFormat.Csv => "CSV files|*.csv",
            ExportFormat.Excel => "Excel files|*.xlsx",
            ExportFormat.Parquet => "Parquet files|*.parquet",
            _ => "All files|*.*"
        };
        var dialog = new SaveFileDialog { Filter = filter, InitialDirectory = _workspace.ExportsFolder, FileName = Workspace.SafeName(obj.Name) + extension };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StartExportProgress();
            SetStatus($"Exporting {obj.Name}...", true);
            await _engine.ExportInMemoryObjectAsync(obj, dialog.FileName, format.Value, new Progress<string>(m => SetStatus(m, true)), CancellationToken.None);
            SetStatus($"Exported {obj.Name} to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed");
            MessageBox.Show(this, ex.Message, "Export In-Memory object", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopExtractProgress();
        }
    }

    private async void MemoryDrop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryObject() is not { } obj) return;
        if (MessageBox.Show(this, $"Drop {obj.ObjectType} \"{obj.QualifiedName}\" from the current memory session?", "Drop In-Memory object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _engine.DropInMemoryObjectAsync(obj, CancellationToken.None);
            await RefreshInMemoryExplorerAsync();
            SetStatus($"Dropped {obj.ObjectType} {obj.Name}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Drop In-Memory object", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MemoryRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshInMemoryExplorerAsync();
    }

    private void RenameScript_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTreeValue(ScriptsTree) is not string path || !File.Exists(path)) return;
        var name = Prompt("Rename SQL", "Script name:", Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(name)) return;
        File.Move(path, Path.Combine(_workspace.ScriptsFolder, Workspace.SafeName(Path.GetFileNameWithoutExtension(name)) + ".sql"), true);
        RefreshExplorer();
    }

    private void DeleteScript_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTreeValue(ScriptsTree) is not string path || !File.Exists(path)) return;
        if (MessageBox.Show(this, $"Delete script \"{Path.GetFileName(path)}\"?", "Delete SQL", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        File.Delete(path);
        RefreshExplorer();
    }

    private void ConnectionsTree_PreviewMouseButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectTreeItemFromEvent(e);
    }

    private void ConnectionsTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SelectTreeItemFromEvent(e);
        OpenConnectionTreeItem();
    }

    private void ConnectionOpen_Click(object sender, RoutedEventArgs e) => OpenConnectionTreeItem();

    private void ConnectionDelete_Click(object sender, RoutedEventArgs e)
    {
        var value = SelectedTreeValue(ConnectionsTree);
        if (value is SavedConnection connection)
        {
            if (MessageBox.Show(this, $"Delete connection \"{connection.Name}\"?", "Delete connection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _workspace.DeleteConnection(connection);
            RefreshExplorer();
        }
        else if (value is ExtractQueryInfo query)
        {
            if (MessageBox.Show(this, $"Delete extract query \"{query.Name}\"?", "Delete extract query", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _workspace.DeleteExtractQuery(query);
            RefreshExplorer();
        }
    }

    private async void OpenConnectionTreeItem()
    {
        var value = SelectedTreeValue(ConnectionsTree);
        if (value is SavedConnection connection)
        {
            var dialog = new ConnectionDialog(_workspace, connection) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            _workspace.UpdateConnection(connection, dialog.ConnectionName, dialog.Kind, dialog.Config, dialog.Secret);
            RefreshExplorer();
            SetStatus($"Updated connection {dialog.ConnectionName}");
        }
        else if (value is ExtractQueryInfo query && File.Exists(query.SqlPath))
        {
            var connections = _workspace.SnapshotConnections();
            var savedConnection = connections.FirstOrDefault(c => c.Id == query.ConnectionId);
            if (savedConnection == null) return;
            var dialog = new ExtractDialog(connections, File.ReadAllText(query.SqlPath), query.Name, savedConnection.Id) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                if (_extractCts != null)
                {
                    MessageBox.Show(this, "An extraction is already running. Stop it before starting another.", "Extract Base Query");
                    return;
                }
                await RunExtractionAsync(dialog.SelectedConnection, dialog.DatasetName, dialog.Sql, dialog.BatchSize, dialog.ExpectedRows);
            }
        }
    }

    private async Task ShowDatasetInfo(DatasetInfo dataset)
    {
        try
        {
            var details = await _engine.GetDatasetDetailsAsync(dataset, CancellationToken.None);
            new DatasetInfoDialog(dataset, details) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Dataset information");
        }
    }

    private async Task RefreshInMemoryExplorerAsync()
    {
        try
        {
            var objects = await _engine.GetInMemoryObjectsAsync(CancellationToken.None);
            _memoryCompletionObjects.Clear();
            _memoryCompletionObjects.AddRange(objects);
            MemoryTree.Items.Clear();
            var groups = objects
                .GroupBy(o => MemoryGroupName(o.ObjectType))
                .OrderBy(g => MemoryGroupSortOrder(g.Key));

            foreach (var group in groups)
            {
                var groupItem = new TreeViewItem { Header = TreeHeader(MemoryGroupIcon(group.Key), group.Key), IsExpanded = true };
                foreach (var obj in group.OrderBy(o => o.Schema).ThenBy(o => o.Name))
                {
                    groupItem.Items.Add(new TreeViewItem
                    {
                        Header = TreeHeader(MemoryObjectIcon(obj), obj.DisplayName),
                        Tag = obj
                    });
                }
                MemoryTree.Items.Add(groupItem);
            }

            if (MemoryTree.Items.Count == 0)
            {
                MemoryTree.Items.Add(new TreeViewItem
                {
                    Header = TreeHeader(MemoryObjectIcon("Empty"), "No user-created memory objects yet"),
                    IsEnabled = false
                });
            }
        }
        catch (Exception ex)
        {
            SetStatus("Unable to refresh In-Memory objects");
            MemoryTree.Items.Clear();
            MemoryTree.Items.Add(new TreeViewItem
            {
                Header = TreeHeader(MemoryObjectIcon("Error"), "Unable to read memory objects: " + ex.Message),
                IsEnabled = false
            });
        }
    }

    private async Task PreviewMemoryObjectAsync(InMemoryObjectInfo obj)
    {
        if (!CanQueryMemoryObject(obj))
        {
            MessageBox.Show(this, "Only tables and views can be previewed.", "Preview In-Memory object");
            return;
        }
        try
        {
            var sql = MemoryObjectSelectSql(obj);
            SqlEditor.Text = sql;
            UsageCombo.SelectedIndex = 0;
            var previewRows = int.TryParse(PreviewRowsText.Text, out var value) ? value : 1000;
            SetStatus($"Previewing {obj.Name}...", true);
            var result = await Task.Run(() => _engine.ExecuteAsync(sql, previewRows, "Memory", CancellationToken.None));
            _currentResults = result.Table;
            ResultsGrid.ItemsSource = result.Table.DefaultView;
            SetStatus($"Previewed {obj.Name}. Displayed {result.DisplayedRows:N0} rows.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Preview In-Memory object", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private InMemoryObjectInfo? SelectedMemoryObject() => SelectedTreeValue(MemoryTree) as InMemoryObjectInfo;

    private static bool CanQueryMemoryObject(InMemoryObjectInfo obj) =>
        obj.ObjectType.Contains("Table", StringComparison.OrdinalIgnoreCase) ||
        obj.ObjectType.Contains("View", StringComparison.OrdinalIgnoreCase);

    private static string MemoryObjectSelectSql(InMemoryObjectInfo obj) =>
        $"select * from {QuoteMemoryQualifiedName(obj.Schema, obj.Name)} limit 100;";

    private static string MemoryObjectSqlSnippet(InMemoryObjectInfo obj)
    {
        if (CanQueryMemoryObject(obj)) return MemoryObjectSelectSql(obj);
        if (obj.ObjectType.Contains("Macro", StringComparison.OrdinalIgnoreCase) ||
            obj.ObjectType.Contains("Function", StringComparison.OrdinalIgnoreCase))
        {
            return $"-- Use this macro/function in a SQL expression.\n-- Adjust arguments as needed.\nselect {QuoteMemoryQualifiedName(obj.Schema, obj.Name)}(' sample ');";
        }
        return $"-- {obj.ObjectType} \"{obj.QualifiedName}\" cannot be selected directly.\n-- Use Information / Schema for details.";
    }

    private static string QuoteMemoryQualifiedName(string schema, string name)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return QuoteMemoryIdentifier(name);
        }
        return $"{QuoteMemoryIdentifier(schema)}.{QuoteMemoryIdentifier(name)}";
    }

    private static string QuoteMemoryIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private void ShowInMemoryObjectInfo(InMemoryObjectDetails details)
    {
        var window = new Window
        {
            Title = "In-Memory object information",
            Width = 720,
            Height = 480,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };
        WindowTitleBar.Apply(window);

        var root = new DockPanel { Margin = new Thickness(14) };
        var summary = new TextBlock
        {
            Text = $"{details.Object.ObjectType}: {details.Object.QualifiedName}" +
                   (details.Object.RowCount.HasValue ? $" ({details.Object.RowCount.Value:N0} rows)" : ""),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        if (!string.IsNullOrWhiteSpace(details.Definition))
        {
            var definition = new TextBox
            {
                Text = details.Definition,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 90,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(definition, Dock.Top);
            root.Children.Add(definition);
        }

        var grid = new DataGrid
        {
            ItemsSource = details.Columns.Select(c => new { c.Name, c.Type, c.Nullable }).ToList(),
            AutoGenerateColumns = true,
            IsReadOnly = true
        };
        root.Children.Add(grid);

        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            MinWidth = 96,
            MinHeight = 32,
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        window.Content = root;
        window.ShowDialog();
    }

    private void RefreshExplorer()
    {
        WorkspaceText.Text = $"Workspace: {_workspace.Root}";
        DatasetsTree.Items.Clear();
        var datasets = _workspace.SnapshotDatasets();
        var connections = _workspace.SnapshotConnections();
        var extractQueries = _workspace.SnapshotExtractQueries();
        foreach (var dataset in datasets.OrderBy(d => d.Name))
        {
            DatasetsTree.Items.Add(new TreeViewItem { Header = TreeHeader(StorageIcon(), $"{dataset.Name} ({dataset.RowCount:N0} rows)"), Tag = dataset });
        }
        ScriptsTree.Items.Clear();
        foreach (var path in _workspace.ListScripts())
        {
            var item = new TreeViewItem { Header = ScriptTreeHeader(path), Tag = path };
            item.Selected += (_, _) =>
            {
                var state = GetScriptState(path);
                state.IsSelected = true;
                UpdateScriptStateUi(state);
            };
            item.Unselected += (_, _) =>
            {
                var state = GetScriptState(path);
                state.IsSelected = false;
                UpdateScriptStateUi(state);
            };
            ScriptsTree.Items.Add(item);
        }
        TemplatesTree.Items.Clear();
        foreach (var path in ListTemplateScripts())
        {
            TemplatesTree.Items.Add(new TreeViewItem { Header = TreeHeader(RulesTemplateIcon(), Path.GetFileNameWithoutExtension(path)), Tag = path });
        }
        ConnectionsTree.Items.Clear();
        foreach (var connection in connections.OrderBy(c => c.Name))
        {
            var item = new TreeViewItem { Header = TreeHeader("\uE839", $"{connection.Name} [{connection.Kind}]"), Tag = connection, IsExpanded = true };
            foreach (var query in extractQueries.Where(q => q.ConnectionId == connection.Id).OrderBy(q => q.Name))
            {
                item.Items.Add(new TreeViewItem { Header = TreeHeader(SqlFileIcon(), $"{query.Name}.sql ({query.RowCount:N0} rows)"), Tag = query });
            }
            ConnectionsTree.Items.Add(item);
        }
        _ = RefreshInMemoryExplorerAsync();
    }

    private void SetStatus(string message, bool busy = false)
    {
        StatusText.Text = busy ? message + "..." : message;
        StopExtractButton.IsEnabled = true;
    }

    private static object? SelectedTreeValue(TreeView tree) => (tree.SelectedItem as TreeViewItem)?.Tag;

    private static void SelectTreeItemFromEvent(RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                return;
            }
        }
    }

    private StackPanel ScriptTreeHeader(string path)
    {
        var state = GetScriptState(path);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 1),
            MinHeight = 24
        };
        var action = new Button
        {
            Content = state.Status is ScriptExecutionStatus.Running or ScriptExecutionStatus.Queued ? "■" : "▶",
            ToolTip = state.Status is ScriptExecutionStatus.Running or ScriptExecutionStatus.Queued ? "Cancel execution" : "Execute SQL file",
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0, 4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = state.Status is ScriptExecutionStatus.Running or ScriptExecutionStatus.Queued
                ? new SolidColorBrush(Color.FromRgb(198, 40, 40))
                : new SolidColorBrush(Color.FromRgb(35, 150, 95))
        };
        action.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleScriptExecution(path);
        };
        header.Children.Add(action);
        header.Children.Add(SqlFileIcon());
        var fileNameText = new TextBlock
        {
            Text = Path.GetFileName(path),
            MinWidth = 135,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(fileNameText);
        var progress = new ProgressBar
        {
            Width = 62,
            Height = 8,
            Margin = new Thickness(8, 0, 6, 0),
            Visibility = state.Status == ScriptExecutionStatus.Running ? Visibility.Visible : Visibility.Collapsed,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 150, 95))
        };
        header.Children.Add(progress);
        var status = new TextBlock
        {
            Text = ScriptStatusText(state),
            Foreground = ScriptStatusBrush(state.Status),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = state.LastError
        };
        header.Children.Add(status);
        state.ActionButton = action;
        state.HeaderPanel = header;
        state.FileNameText = fileNameText;
        state.Progress = progress;
        state.StatusText = status;
        UpdateScriptStateUi(state);
        return header;
    }

    private ScriptExecutionState GetScriptState(string path)
    {
        if (_scriptExecutionStates.TryGetValue(path, out var state)) return state;
        state = new ScriptExecutionState { Path = path };
        _scriptExecutionStates[path] = state;
        return state;
    }

    private void ToggleScriptExecution(string path)
    {
        var state = GetScriptState(path);
        if (state.Status is ScriptExecutionStatus.Running or ScriptExecutionStatus.Queued)
        {
            CancelScriptExecution(state);
            return;
        }

        QueueScriptExecution(state);
    }

    private void QueueScriptExecution(ScriptExecutionState state)
    {
        if (!File.Exists(state.Path))
        {
            state.Status = ScriptExecutionStatus.Failed;
            state.LastError = "SQL script file does not exist.";
            state.StatusMessage = "Failed";
            UpdateScriptStateUi(state);
            return;
        }

        state.Cancellation?.Dispose();
        state.Cancellation = new CancellationTokenSource();
        state.LastError = null;
        state.RowCount = null;
        state.Elapsed = null;
        state.Status = ScriptExecutionStatus.Queued;
        state.StatusMessage = "Queued";
        _scriptExecutionQueue.Enqueue(state.Path);
        UpdateQueuedScriptPositions();
        UpdateScriptStateUi(state);
        PumpScriptExecutionQueue();
    }

    private void CancelScriptExecution(ScriptExecutionState state)
    {
        if (state.Status == ScriptExecutionStatus.Queued)
        {
            state.Status = ScriptExecutionStatus.Cancelled;
            state.StatusMessage = "Cancelled";
            state.Cancellation?.Cancel();
            state.Cancellation?.Dispose();
            state.Cancellation = null;
            UpdateQueuedScriptPositions();
            UpdateScriptStateUi(state);
            return;
        }

        if (state.Status == ScriptExecutionStatus.Running)
        {
            state.StatusMessage = "Cancelling...";
            state.Cancellation?.Cancel();
            UpdateScriptStateUi(state);
        }
    }

    private void PumpScriptExecutionQueue()
    {
        while (_activeScriptExecutions < MaxScriptExecutions && _scriptExecutionQueue.Count > 0)
        {
            var path = _scriptExecutionQueue.Dequeue();
            var state = GetScriptState(path);
            if (state.Status != ScriptExecutionStatus.Queued) continue;
            StartScriptExecution(state);
        }

        UpdateQueuedScriptPositions();
    }

    private async void StartScriptExecution(ScriptExecutionState state)
    {
        _activeScriptExecutions++;
        state.Status = ScriptExecutionStatus.Running;
        state.StatusMessage = "Running...";
        state.Elapsed = null;
        var stopwatch = Stopwatch.StartNew();
        UpdateScriptStateUi(state);
        try
        {
            var sql = File.ReadAllText(state.Path);
            var token = state.Cancellation?.Token ?? CancellationToken.None;
            var summary = await _engine.ExecuteScriptSummaryAsync(sql, token);
            stopwatch.Stop();
            state.RowCount = summary.RowCount;
            state.Elapsed = stopwatch.Elapsed;
            state.Status = ScriptExecutionStatus.Completed;
            state.StatusMessage = summary.ReturnedRows
                ? $"{summary.RowCount.GetValueOrDefault():N0} rows · {FormatElapsed(stopwatch.Elapsed)}"
                : $"Completed · {FormatElapsed(stopwatch.Elapsed)}";
            SetStatus($"SQL script completed: {Path.GetFileName(state.Path)}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            state.Elapsed = stopwatch.Elapsed;
            state.Status = ScriptExecutionStatus.Cancelled;
            state.StatusMessage = "Cancelled";
            SetStatus($"SQL script cancelled: {Path.GetFileName(state.Path)}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            state.Elapsed = stopwatch.Elapsed;
            state.Status = ScriptExecutionStatus.Failed;
            state.StatusMessage = "Failed";
            state.LastError = ex.Message;
            SetStatus($"SQL script failed: {Path.GetFileName(state.Path)}");
        }
        finally
        {
            state.Cancellation?.Dispose();
            state.Cancellation = null;
            _activeScriptExecutions = Math.Max(0, _activeScriptExecutions - 1);
            UpdateScriptStateUi(state);
            PumpScriptExecutionQueue();
        }
    }

    private void UpdateQueuedScriptPositions()
    {
        var queued = _scriptExecutionQueue
            .Select(GetScriptState)
            .Where(s => s.Status == ScriptExecutionStatus.Queued)
            .ToList();
        for (var i = 0; i < queued.Count; i++)
        {
            queued[i].StatusMessage = $"Queued #{i + 1}";
            UpdateScriptStateUi(queued[i]);
        }
    }

    private static string ScriptStatusText(ScriptExecutionState state) => state.Status switch
    {
        ScriptExecutionStatus.Ready => "Ready",
        ScriptExecutionStatus.Queued => state.StatusMessage,
        ScriptExecutionStatus.Running => state.StatusMessage,
        ScriptExecutionStatus.Completed => state.StatusMessage,
        ScriptExecutionStatus.Failed => state.StatusMessage,
        ScriptExecutionStatus.Cancelled => state.StatusMessage,
        _ => ""
    };

    private static void UpdateScriptStateUi(ScriptExecutionState state)
    {
        if (state.ActionButton != null)
        {
            var active = state.Status is ScriptExecutionStatus.Running or ScriptExecutionStatus.Queued;
            state.ActionButton.Content = active ? "■" : "▶";
            state.ActionButton.ToolTip = active ? "Cancel execution" : "Execute SQL file";
            state.ActionButton.Foreground = active
                ? new SolidColorBrush(Color.FromRgb(198, 40, 40))
                : new SolidColorBrush(Color.FromRgb(35, 150, 95));
        }

        if (state.Progress != null)
        {
            state.Progress.Visibility = state.Status == ScriptExecutionStatus.Running ? Visibility.Visible : Visibility.Collapsed;
            state.Progress.IsIndeterminate = state.Status == ScriptExecutionStatus.Running;
        }

        if (state.StatusText != null)
        {
            state.StatusText.Text = ScriptStatusText(state);
            state.StatusText.Foreground = state.IsSelected ? SystemColors.HighlightTextBrush : ScriptStatusBrush(state.Status);
            state.StatusText.ToolTip = state.LastError;
        }

        if (state.FileNameText != null)
        {
            if (state.IsSelected)
            {
                state.FileNameText.Foreground = SystemColors.HighlightTextBrush;
            }
            else
            {
                state.FileNameText.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        if (state.HeaderPanel != null)
        {
            if (state.IsSelected)
            {
                state.HeaderPanel.Background = SystemColors.HighlightBrush;
            }
            else
            {
                state.HeaderPanel.ClearValue(Panel.BackgroundProperty);
            }
        }
    }

    private static Brush ScriptStatusBrush(ScriptExecutionStatus status) => status switch
    {
        ScriptExecutionStatus.Completed => new SolidColorBrush(Color.FromRgb(35, 150, 95)),
        ScriptExecutionStatus.Failed => new SolidColorBrush(Color.FromRgb(198, 40, 40)),
        ScriptExecutionStatus.Cancelled => new SolidColorBrush(Color.FromRgb(88, 88, 88)),
        ScriptExecutionStatus.Running => new SolidColorBrush(Color.FromRgb(35, 150, 95)),
        ScriptExecutionStatus.Queued => new SolidColorBrush(Color.FromRgb(88, 88, 88)),
        _ => new SolidColorBrush(Color.FromRgb(88, 88, 88))
    };

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? elapsed.ToString(@"hh\:mm\:ss")
        : elapsed.ToString(@"mm\:ss");

    private static StackPanel TreeHeader(string glyph, string text)
    {
        return TreeHeader(GlyphIcon(glyph), text);
    }

    private static StackPanel TreeHeader(UIElement icon, string text)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(icon);
        header.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        });
        return header;
    }

    private static TextBlock GlyphIcon(string glyph)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Grid SqlFileIcon()
    {
        var blue = new SolidColorBrush(Color.FromRgb(43, 119, 210));
        var lightBlue = new SolidColorBrush(Color.FromRgb(219, 235, 255));
        var foldBlue = new SolidColorBrush(Color.FromRgb(157, 199, 245));
        var darkBlue = new SolidColorBrush(Color.FromRgb(26, 83, 155));
        var icon = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        icon.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 12,
            Height = 15,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = lightBlue,
            Stroke = darkBlue,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        icon.Children.Add(new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection { new Point(10, 1), new Point(14, 5), new Point(10, 5) },
            Fill = foldBlue,
            Stroke = darkBlue,
            StrokeThickness = 0.8
        });
        icon.Children.Add(new TextBlock
        {
            Text = "SQL",
            Foreground = blue,
            FontSize = 5.5,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        });
        return icon;
    }

    private static Grid RulesTemplateIcon()
    {
        var purple = new SolidColorBrush(Color.FromRgb(116, 77, 169));
        var orange = new SolidColorBrush(Color.FromRgb(238, 130, 36));
        var green = new SolidColorBrush(Color.FromRgb(35, 150, 95));
        var blue = new SolidColorBrush(Color.FromRgb(43, 119, 210));
        var lightPurple = new SolidColorBrush(Color.FromRgb(241, 235, 250));
        var lineBrush = new SolidColorBrush(Color.FromRgb(88, 88, 88));
        var icon = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        icon.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 14,
            Height = 14,
            RadiusX = 2,
            RadiusY = 2,
            Fill = lightPurple,
            Stroke = purple,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        AddRuleDot(icon, orange, 4, 4);
        AddRuleDot(icon, green, 4, 8);
        AddRuleDot(icon, blue, 4, 12);
        AddRuleLine(icon, lineBrush, 7, 4);
        AddRuleLine(icon, lineBrush, 7, 8);
        AddRuleLine(icon, lineBrush, 7, 12);
        return icon;
    }

    private static void AddRuleDot(Grid icon, Brush fill, double left, double top)
    {
        icon.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 3,
            Height = 3,
            Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top, 0, 0)
        });
    }

    private static void AddRuleLine(Grid icon, Brush fill, double left, double top)
    {
        icon.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 5,
            Height = 1.2,
            RadiusX = 0.6,
            RadiusY = 0.6,
            Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top + 0.8, 0, 0)
        });
    }

    private static Grid StorageIcon()
    {
        var teal = new SolidColorBrush(Color.FromRgb(0, 120, 140));
        var lightTeal = new SolidColorBrush(Color.FromRgb(111, 207, 222));
        var darkTeal = new SolidColorBrush(Color.FromRgb(0, 84, 98));
        var icon = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        icon.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 14,
            Height = 9,
            Fill = teal,
            Stroke = darkTeal,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 1)
        });
        icon.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 6,
            Fill = lightTeal,
            Stroke = darkTeal,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0)
        });
        icon.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 6,
            Fill = teal,
            Stroke = darkTeal,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        });
        return icon;
    }

    private static string MemoryGroupName(string objectType)
    {
        if (objectType.Contains("Table", StringComparison.OrdinalIgnoreCase)) return "Tables";
        if (objectType.Contains("View", StringComparison.OrdinalIgnoreCase)) return "Views";
        if (objectType.Contains("Macro", StringComparison.OrdinalIgnoreCase) || objectType.Contains("Function", StringComparison.OrdinalIgnoreCase)) return "Macros / Functions";
        if (objectType.Contains("Index", StringComparison.OrdinalIgnoreCase)) return "Indexes";
        if (objectType.Contains("Sequence", StringComparison.OrdinalIgnoreCase)) return "Sequences";
        if (objectType.Contains("Schema", StringComparison.OrdinalIgnoreCase)) return "Schemas";
        return "Other";
    }

    private static int MemoryGroupSortOrder(string group) => group switch
    {
        "Tables" => 0,
        "Views" => 1,
        "Macros / Functions" => 2,
        "Indexes" => 3,
        "Sequences" => 4,
        "Schemas" => 5,
        _ => 99
    };

    private static UIElement MemoryGroupIcon(string group) => group switch
    {
        "Tables" => MemoryObjectIcon("Table"),
        "Views" => MemoryObjectIcon("View"),
        "Macros / Functions" => MemoryObjectIcon("Macro"),
        "Indexes" => MemoryObjectIcon("Index"),
        "Sequences" => MemoryObjectIcon("Sequence"),
        "Schemas" => MemoryObjectIcon("Schema"),
        _ => MemoryObjectIcon("Other")
    };

    private static UIElement MemoryObjectIcon(InMemoryObjectInfo obj) => MemoryObjectIcon(obj.ObjectType);

    private static Grid MemoryObjectIcon(string objectType)
    {
        if (objectType.Contains("Table", StringComparison.OrdinalIgnoreCase)) return TableGridIcon();
        if (objectType.Contains("View", StringComparison.OrdinalIgnoreCase)) return EyeIcon();
        if (objectType.Contains("Macro", StringComparison.OrdinalIgnoreCase) || objectType.Contains("Function", StringComparison.OrdinalIgnoreCase)) return FunctionIcon();
        if (objectType.Contains("Index", StringComparison.OrdinalIgnoreCase)) return KeyIcon();
        if (objectType.Contains("Sequence", StringComparison.OrdinalIgnoreCase)) return HashIcon();
        if (objectType.Contains("Schema", StringComparison.OrdinalIgnoreCase)) return FolderIcon();
        if (objectType.Contains("Error", StringComparison.OrdinalIgnoreCase)) return BadgeIcon("!", Color.FromRgb(198, 40, 40), Color.FromRgb(255, 235, 238));
        if (objectType.Contains("Empty", StringComparison.OrdinalIgnoreCase)) return BadgeIcon("i", Color.FromRgb(88, 88, 88), Color.FromRgb(245, 245, 245));
        return BadgeIcon("•", Color.FromRgb(88, 88, 88), Color.FromRgb(245, 245, 245));
    }

    private static Grid TableGridIcon()
    {
        var blue = new SolidColorBrush(Color.FromRgb(43, 119, 210));
        var light = new SolidColorBrush(Color.FromRgb(219, 235, 255));
        var icon = IconCanvas();
        icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 14, Height = 14, RadiusX = 2, RadiusY = 2, Fill = light, Stroke = blue, StrokeThickness = 1, Margin = new Thickness(1) });
        for (var i = 5; i <= 10; i += 5)
        {
            icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 12, Height = 1, Fill = blue, Opacity = 0.75, Margin = new Thickness(2, i, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
            icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 1, Height = 12, Fill = blue, Opacity = 0.75, Margin = new Thickness(i, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        }
        return icon;
    }

    private static Grid EyeIcon()
    {
        var purple = new SolidColorBrush(Color.FromRgb(116, 77, 169));
        var light = new SolidColorBrush(Color.FromRgb(241, 235, 250));
        var icon = IconCanvas();
        icon.Children.Add(new System.Windows.Shapes.Ellipse { Width = 15, Height = 9, Fill = light, Stroke = purple, StrokeThickness = 1.2, Margin = new Thickness(0.5, 3.5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        icon.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4.5, Height = 4.5, Fill = purple, Margin = new Thickness(5.75, 5.75, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        return icon;
    }

    private static Grid FunctionIcon() => BadgeIcon("ƒx", Color.FromRgb(35, 150, 95), Color.FromRgb(229, 246, 239), 8.5);

    private static Grid KeyIcon()
    {
        var orange = new SolidColorBrush(Color.FromRgb(238, 130, 36));
        var light = new SolidColorBrush(Color.FromRgb(255, 243, 224));
        var icon = IconCanvas();
        icon.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = light, Stroke = orange, StrokeThickness = 1.3, Margin = new Thickness(1.5, 4.5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 8, Height = 2, Fill = orange, Margin = new Thickness(7, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 2, Height = 4, Fill = orange, Margin = new Thickness(12, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        return icon;
    }

    private static Grid HashIcon() => BadgeIcon("#", Color.FromRgb(88, 88, 88), Color.FromRgb(242, 244, 248), 10);

    private static Grid FolderIcon()
    {
        var yellow = new SolidColorBrush(Color.FromRgb(242, 177, 52));
        var dark = new SolidColorBrush(Color.FromRgb(172, 118, 20));
        var icon = IconCanvas();
        icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 7, Height = 3, RadiusX = 1, RadiusY = 1, Fill = yellow, Stroke = dark, StrokeThickness = 0.8, Margin = new Thickness(1.5, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        icon.Children.Add(new System.Windows.Shapes.Rectangle { Width = 14, Height = 10, RadiusX = 1.5, RadiusY = 1.5, Fill = yellow, Stroke = dark, StrokeThickness = 1, Margin = new Thickness(1, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        return icon;
    }

    private static Grid BadgeIcon(string text, Color color, Color background, double fontSize = 9)
    {
        var icon = IconCanvas();
        icon.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 15,
            Height = 15,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(background),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            Margin = new Thickness(0.5)
        });
        icon.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(color),
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return icon;
    }

    private static Grid IconCanvas() => new()
    {
        Width = 16,
        Height = 16,
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static IEnumerable<string> ListTemplateScripts()
    {
        var templateFolder = Path.Combine(AppContext.BaseDirectory, "Templates");
        return Directory.Exists(templateFolder)
            ? Directory.EnumerateFiles(templateFolder, "*.sql").OrderBy(Path.GetFileName)
            : [];
    }

    private enum ScriptExecutionStatus
    {
        Ready,
        Queued,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    private sealed class ScriptExecutionState
    {
        public string Path { get; init; } = "";
        public ScriptExecutionStatus Status { get; set; } = ScriptExecutionStatus.Ready;
        public string StatusMessage { get; set; } = "Ready";
        public long? RowCount { get; set; }
        public TimeSpan? Elapsed { get; set; }
        public string? LastError { get; set; }
        public bool IsSelected { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public Button? ActionButton { get; set; }
        public StackPanel? HeaderPanel { get; set; }
        public TextBlock? FileNameText { get; set; }
        public ProgressBar? Progress { get; set; }
        public TextBlock? StatusText { get; set; }
    }

    private sealed record SqlSuggestion(string Text, string InsertText, string Description, double Priority, string? Placeholder = null);

    private sealed class SqlCompletionData : ICompletionData
    {
        private readonly SqlSuggestion _suggestion;

        public SqlCompletionData(SqlSuggestion suggestion)
        {
            _suggestion = suggestion;
        }

        public ImageSource? Image => null;
        public string Text => _suggestion.InsertText;
        public object Content => _suggestion.Text;
        public object Description => _suggestion.Description;
        public double Priority => _suggestion.Priority;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var startOffset = completionSegment.Offset;
            textArea.Document.Replace(completionSegment, _suggestion.InsertText);
            if (!string.IsNullOrWhiteSpace(_suggestion.Placeholder))
            {
                var placeholderOffset = _suggestion.InsertText.IndexOf(_suggestion.Placeholder, StringComparison.Ordinal);
                if (placeholderOffset >= 0)
                {
                    textArea.Caret.Offset = startOffset + placeholderOffset;
                    textArea.Selection = Selection.Create(textArea, startOffset + placeholderOffset, startOffset + placeholderOffset + _suggestion.Placeholder.Length);
                    return;
                }
            }
            textArea.Caret.Offset = startOffset + _suggestion.InsertText.Length;
        }
    }

    private sealed class DuckDbSqlColorizer : DocumentColorizingTransformer
    {
        private static readonly Regex TokenRegex = new(@"\b[a-zA-Z_][a-zA-Z0-9_]*\b", RegexOptions.Compiled);
        private static readonly Regex StringRegex = new(@"'([^']|'')*'", RegexOptions.Compiled);
        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "join", "left", "right", "inner", "outer", "full", "cross", "on", "using",
            "group", "by", "order", "having", "limit", "offset", "create", "or", "replace", "temp", "temporary",
            "table", "view", "macro", "index", "sequence", "schema", "as", "drop", "if", "exists", "insert",
            "into", "values", "copy", "to", "format", "parquet", "csv", "compression", "zstd", "with", "case",
            "when", "then", "else", "end", "and", "not", "is", "null", "true", "false", "distinct", "union",
            "all", "except", "intersect", "window", "over", "partition", "rows", "range", "between", "cast",
            "try_cast", "describe", "show", "attach", "detach", "database"
        };
        private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
        {
            "count", "sum", "avg", "min", "max", "coalesce", "nullif", "upper", "lower", "trim", "regexp_replace",
            "regexp_matches", "strptime", "strftime", "round", "abs", "length", "substring", "replace",
            "split_part", "date_diff", "date_trunc", "row_number", "rank", "dense_rank", "nextval", "read_parquet",
            "read_csv_auto"
        };

        protected override void ColorizeLine(DocumentLine line)
        {
            var lineText = CurrentContext.Document.GetText(line);
            var lineStart = line.Offset;
            var commentIndex = lineText.IndexOf("--", StringComparison.Ordinal);
            var codeLength = commentIndex >= 0 ? commentIndex : lineText.Length;

            foreach (Match match in StringRegex.Matches(lineText[..codeLength]))
            {
                ChangeSafe(lineStart + match.Index, lineStart + match.Index + match.Length, Brushes.Brown, FontWeights.Normal);
            }

            foreach (Match match in TokenRegex.Matches(lineText[..codeLength]))
            {
                var token = match.Value;
                if (Keywords.Contains(token))
                {
                    ChangeSafe(lineStart + match.Index, lineStart + match.Index + match.Length, new SolidColorBrush(Color.FromRgb(0, 82, 155)), FontWeights.Bold);
                }
                else if (Functions.Contains(token))
                {
                    ChangeSafe(lineStart + match.Index, lineStart + match.Index + match.Length, new SolidColorBrush(Color.FromRgb(0, 120, 140)), FontWeights.Normal);
                }
            }

            if (commentIndex >= 0)
            {
                ChangeSafe(lineStart + commentIndex, line.EndOffset, new SolidColorBrush(Color.FromRgb(0, 128, 0)), FontWeights.Normal);
            }
        }

        private void ChangeSafe(int startOffset, int endOffset, Brush foreground, FontWeight weight)
        {
            if (endOffset <= startOffset) return;
            ChangeLinePart(startOffset, endOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(foreground);
                element.TextRunProperties.SetTypeface(new Typeface(
                    element.TextRunProperties.Typeface.FontFamily,
                    element.TextRunProperties.Typeface.Style,
                    weight,
                    element.TextRunProperties.Typeface.Stretch));
            });
        }
    }

    private static string? Prompt(string title, string label, string defaultValue)
    {
        var window = new Window
        {
            Title = title,
            Width = 440,
            MinHeight = 220,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        WindowTitleBar.Apply(window);
        var panel = new StackPanel { Margin = new Thickness(16) };
        var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 6, 0, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 6) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 96, MinHeight = 32, Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, MinHeight = 32, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        window.Content = panel;
        return window.ShowDialog() == true ? input.Text : null;
    }

    private void StartExportProgress()
    {
        ExtractProgressBar.Visibility = Visibility.Visible;
        ExtractProgressBar.Value = 0;
        ExtractProgressBar.IsIndeterminate = true;
    }

    private ExportFormat? PromptExportFormat()
    {
        var window = new Window
        {
            Title = "Export Result",
            Width = 430,
            MinHeight = 260,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this
        };
        WindowTitleBar.Apply(window);
        var panel = new StackPanel { Margin = new Thickness(16) };
        var combo = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 6, 0, 12) };
        combo.Items.Add("CSV");
        combo.Items.Add("EXCEL");
        combo.Items.Add("PARQUET");
        combo.SelectedIndex = 0;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 6) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 96, MinHeight = 32, Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, MinHeight = 32, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(new TextBlock { Text = "Export format:" });
        panel.Children.Add(combo);
        panel.Children.Add(new TextBlock { Text = "CSV and Parquet are streamed directly by DuckDB. Excel is streamed to .xlsx and is limited to 1,048,576 rows.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        window.Content = panel;
        if (window.ShowDialog() != true) return null;
        return combo.SelectedItem?.ToString() switch
        {
            "CSV" => ExportFormat.Csv,
            "EXCEL" => ExportFormat.Excel,
            "PARQUET" => ExportFormat.Parquet,
            _ => null
        };
    }
}
