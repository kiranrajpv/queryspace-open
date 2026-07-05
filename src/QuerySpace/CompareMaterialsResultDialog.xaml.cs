using System.Windows;

namespace QuerySpace;

public partial class CompareMaterialsResultDialog : Window
{
    public CompareMaterialsResultDialog(MaterialCompareResult result)
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        SummaryText.Text = result.SavedDataset == null
            ? $"Missing mandatory materials: {result.MissingRowsCount:N0}. Coverage: {result.FoundRows:N0}/{result.ReferenceRows:N0}."
            : $"Missing mandatory materials: {result.MissingRowsCount:N0}. Saved as dataset {result.SavedDataset.Name}.";
        MissingGrid.ItemsSource = result.MissingRows.DefaultView;
        CoverageGrid.ItemsSource = result.Coverage.DefaultView;
        ReasonGrid.ItemsSource = result.ReasonSummary.DefaultView;
        ImpactGrid.ItemsSource = result.ColumnImpact.DefaultView;
        SqlText.Text = result.SqlUsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
