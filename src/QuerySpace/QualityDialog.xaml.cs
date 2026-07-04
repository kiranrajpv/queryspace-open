using System.Windows;

namespace QuerySpace;

public partial class QualityDialog : Window
{
    public QualityDialog(QualityReport report)
    {
        InitializeComponent();
        SummaryText.Text = $"Quality score: {report.QualityScore}    Rows analyzed: {report.RowsAnalyzed:N0}    Numeric outliers: {report.NumericOutliers:N0}";
        ColumnsGrid.ItemsSource = report.Columns;
    }
}
