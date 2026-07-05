using System.Windows;

namespace QuerySpace;

public partial class TransformationAnalysisDialog : Window
{
    public TransformationAnalysisDialog(TransformationAnalysisReport report)
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        SummaryText.Text = $"{report.AnalysisName}: score {report.QualityScore:N1}; rows analyzed {report.RowsAnalyzed:N0}; issues {report.Issues.Rows.Count:N0}.";
        OverviewGrid.ItemsSource = report.Overview.DefaultView;
        IssuesGrid.ItemsSource = report.Issues.DefaultView;
        PairsGrid.ItemsSource = report.ColumnPairs.DefaultView;
        XrefGrid.ItemsSource = report.CrossReference.DefaultView;
        RecommendationsGrid.ItemsSource = report.Recommendations.DefaultView;
        TagsText.Text = string.Join(Environment.NewLine, report.Tags.OrderBy(kv => kv.Key).Select(kv => $"@{kv.Key}: {kv.Value}"));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
