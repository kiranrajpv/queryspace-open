using Microsoft.Win32;
using System.Diagnostics;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private int _activeScriptExecutions;

    public MainWindow()
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        _engine = new DataEngine(_workspace);
        RefreshExplorer();
        SetStatus("Ready");
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
            SetStatus($"SQL completed. Displayed {result.DisplayedRows:N0} preview rows.");
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

    private void ConnectionsTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenConnectionTreeItem();
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
    }

    private void SetStatus(string message, bool busy = false)
    {
        StatusText.Text = busy ? message + "..." : message;
        StopExtractButton.IsEnabled = true;
    }

    private static object? SelectedTreeValue(TreeView tree) => (tree.SelectedItem as TreeViewItem)?.Tag;

    private StackPanel ScriptTreeHeader(string path)
    {
        var state = GetScriptState(path);
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
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
            state.FileNameText.Foreground = state.IsSelected ? SystemColors.HighlightTextBrush : SystemColors.ControlTextBrush;
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
        public TextBlock? FileNameText { get; set; }
        public ProgressBar? Progress { get; set; }
        public TextBlock? StatusText { get; set; }
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
