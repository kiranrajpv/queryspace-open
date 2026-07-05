using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace QuerySpace;

public partial class CompareMaterialsDialog : Window
{
    private readonly DataEngine _engine;
    private readonly List<DatasetInfo> _datasets;
    private readonly ObservableCollection<SelectableColumn> _explainColumns = new();

    public CompareMaterialsDialog(DataEngine engine, IReadOnlyList<DatasetInfo> datasets)
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        _engine = engine;
        _datasets = datasets.OrderBy(d => d.Name).ToList();

        ReferenceDatasetCombo.ItemsSource = _datasets;
        TargetDatasetCombo.ItemsSource = _datasets;
        ContextDatasetCombo.ItemsSource = new[] { new DatasetInfo { Name = "(none)" } }.Concat(_datasets).ToList();
        ContextDatasetCombo.SelectedIndex = 0;
        ExplainColumnsList.ItemsSource = _explainColumns;

        if (_datasets.Count > 0) ReferenceDatasetCombo.SelectedIndex = 0;
        if (_datasets.Count > 1) TargetDatasetCombo.SelectedIndex = 1;
        UpdateOutputName();
    }

    public MaterialCompareRequest? Request { get; private set; }

    private async void DatasetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadColumnsAsync();
        UpdateOutputName();
    }

    private async Task LoadColumnsAsync()
    {
        if (ReferenceDatasetCombo.SelectedItem is not DatasetInfo reference || TargetDatasetCombo.SelectedItem is not DatasetInfo target) return;

        try
        {
            var referenceDetails = await _engine.GetDatasetDetailsAsync(reference, CancellationToken.None);
            var targetDetails = await _engine.GetDatasetDetailsAsync(target, CancellationToken.None);
            var referenceColumns = referenceDetails.Columns.Select(c => c.Name).ToList();
            var targetColumns = targetDetails.Columns.Select(c => c.Name).ToList();

            SetComboItems(ReferenceKey1Combo, referenceColumns);
            SetComboItems(ReferenceKey2Combo, new[] { "" }.Concat(referenceColumns).ToList());
            SetComboItems(TargetKey1Combo, targetColumns);
            SetComboItems(TargetKey2Combo, new[] { "" }.Concat(targetColumns).ToList());

            AutoSelectKey(ReferenceKey1Combo, referenceColumns, "material", "matnr", "material_id");
            AutoSelectMatchingTarget(TargetKey1Combo, targetColumns, ReferenceKey1Combo.Text, "material", "matnr", "material_id");
            AutoSelectKey(ReferenceKey2Combo, new[] { "" }.Concat(referenceColumns).ToList(), "plant", "werks");
            AutoSelectMatchingTarget(TargetKey2Combo, new[] { "" }.Concat(targetColumns).ToList(), ReferenceKey2Combo.Text, "plant", "werks");

            _explainColumns.Clear();
            foreach (var column in referenceColumns)
            {
                var selected = IsLikelyExplainColumn(column);
                _explainColumns.Add(new SelectableColumn(column, selected));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load dataset columns", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceDatasetCombo.SelectedItem is not DatasetInfo reference || TargetDatasetCombo.SelectedItem is not DatasetInfo target)
        {
            MessageBox.Show(this, "Select both a reference and target dataset.", "Compare mandatory materials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (reference.Id == target.Id)
        {
            MessageBox.Show(this, "Reference and target datasets must be different.", "Compare mandatory materials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var mappings = new List<MaterialCompareKeyMapping>();
        AddMappingIfPresent(mappings, ReferenceKey1Combo.Text, TargetKey1Combo.Text);
        AddMappingIfPresent(mappings, ReferenceKey2Combo.Text, TargetKey2Combo.Text);
        if (mappings.Count == 0)
        {
            MessageBox.Show(this, "Select at least one key mapping.", "Compare mandatory materials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var context = ContextDatasetCombo.SelectedItem as DatasetInfo;
        if (context != null && context.Name == "(none)") context = null;
        var previewRows = int.TryParse(PreviewRowsText.Text, out var rows) ? Math.Max(1, rows) : 1000;
        Request = new MaterialCompareRequest
        {
            ReferenceDataset = reference,
            TargetDataset = target,
            ContextDataset = context,
            KeyMappings = mappings,
            ExplainColumns = _explainColumns.Where(c => c.IsSelected).Select(c => c.Name).ToList(),
            NormalizeKeys = NormalizeKeysCheck.IsChecked == true,
            SaveMissingAsDataset = SaveMissingCheck.IsChecked == true,
            OutputDatasetName = OutputDatasetNameText.Text,
            PreviewRows = previewRows
        };
        DialogResult = true;
    }

    private void UpdateOutputName()
    {
        if (ReferenceDatasetCombo.SelectedItem is DatasetInfo reference && TargetDatasetCombo.SelectedItem is DatasetInfo target)
        {
            OutputDatasetNameText.Text = Workspace.SafeName($"missing_{reference.Name}_vs_{target.Name}");
        }
    }

    private static void AddMappingIfPresent(List<MaterialCompareKeyMapping> mappings, string referenceColumn, string targetColumn)
    {
        if (string.IsNullOrWhiteSpace(referenceColumn) && string.IsNullOrWhiteSpace(targetColumn)) return;
        mappings.Add(new MaterialCompareKeyMapping
        {
            ReferenceColumn = referenceColumn.Trim(),
            TargetColumn = targetColumn.Trim()
        });
    }

    private static void SetComboItems(ComboBox combo, IReadOnlyList<string> values)
    {
        combo.ItemsSource = values;
        combo.SelectedIndex = values.Count > 0 ? 0 : -1;
    }

    private static void AutoSelectKey(ComboBox combo, IReadOnlyList<string> columns, params string[] preferred)
    {
        var match = columns.FirstOrDefault(c => preferred.Any(p => c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.Contains(p, StringComparison.OrdinalIgnoreCase)));
        if (match != null) combo.SelectedItem = match;
    }

    private static void AutoSelectMatchingTarget(ComboBox combo, IReadOnlyList<string> columns, string referenceColumn, params string[] fallback)
    {
        var match = columns.FirstOrDefault(c => c.Equals(referenceColumn, StringComparison.OrdinalIgnoreCase))
            ?? columns.FirstOrDefault(c => fallback.Any(p => c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.Contains(p, StringComparison.OrdinalIgnoreCase)));
        if (match != null) combo.SelectedItem = match;
    }

    private static bool IsLikelyExplainColumn(string column)
    {
        var c = column.ToLowerInvariant();
        return c.Contains("plant") || c.Contains("werks") || c.Contains("type") || c.Contains("status") ||
               c.Contains("flag") || c.Contains("delete") || c.Contains("valid") || c.Contains("source") ||
               c.Contains("group") || c.Contains("mrp") || c.Contains("controller");
    }

    private sealed class SelectableColumn : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SelectableColumn(string name, bool isSelected)
        {
            Name = name;
            _isSelected = isSelected;
        }

        public string Name { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
