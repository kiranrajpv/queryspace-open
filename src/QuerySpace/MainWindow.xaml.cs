using Microsoft.Win32;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace QuerySpace;

public partial class MainWindow : Window
{
    private Workspace _workspace = new();
    private DataEngine _engine;
    private CancellationTokenSource? _extractCts;
    private DataTable? _currentResults;

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
        if (_workspace.State.Connections.Count == 0)
        {
            MessageBox.Show(this, "Add a database connection first.", "Extract Base Query");
            return;
        }
        var dialog = new ExtractDialog(_workspace.State.Connections, SqlEditor.Text) { Owner = this };
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

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", InitialDirectory = _workspace.ExportsFolder, FileName = "query-results.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetStatus("Exporting CSV...", true);
            await _engine.ExportCsvAsync(SqlEditor.Text, dialog.FileName, CancellationToken.None);
            SetStatus($"Exported {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed");
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var savedConnection = _workspace.State.Connections.FirstOrDefault(c => c.Id == query.ConnectionId);
            if (savedConnection == null) return;
            var dialog = new ExtractDialog(_workspace.State.Connections, File.ReadAllText(query.SqlPath), query.Name, savedConnection.Id) { Owner = this };
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
        foreach (var dataset in _workspace.State.Datasets.OrderBy(d => d.Name))
        {
            DatasetsTree.Items.Add(new TreeViewItem { Header = $"{dataset.Name} ({dataset.RowCount:N0} rows)", Tag = dataset });
        }
        ScriptsTree.Items.Clear();
        foreach (var path in _workspace.ListScripts())
        {
            ScriptsTree.Items.Add(new TreeViewItem { Header = Path.GetFileName(path), Tag = path });
        }
        ConnectionsTree.Items.Clear();
        foreach (var connection in _workspace.State.Connections.OrderBy(c => c.Name))
        {
            var item = new TreeViewItem { Header = $"{connection.Name} [{connection.Kind}]", Tag = connection, IsExpanded = true };
            foreach (var query in _workspace.State.ExtractQueries.Where(q => q.ConnectionId == connection.Id).OrderBy(q => q.Name))
            {
                item.Items.Add(new TreeViewItem { Header = $"{query.Name}.sql ({query.RowCount:N0} rows)", Tag = query });
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

    private static string? Prompt(string title, string label, string defaultValue)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 185,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        WindowTitleBar.Apply(window);
        var panel = new StackPanel { Margin = new Thickness(12) };
        var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 6, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
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
}
