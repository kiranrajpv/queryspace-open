using System.Windows;

namespace QuerySpace;

public partial class DatasetInfoDialog : Window
{
    public DatasetInfoDialog(DatasetInfo dataset, DatasetDetails details)
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        TitleText.Text = dataset.Name;
        PathText.Text = details.Path;
        StatsText.Text = $"Rows: {details.RowCount:N0}    File size: {details.FileSizeBytes / 1024.0 / 1024.0:N2} MB";
        ColumnsGrid.ItemsSource = details.Columns;
    }
}
