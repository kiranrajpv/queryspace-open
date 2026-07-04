using System.Windows;

namespace QuerySpace;

public partial class ExtractDialog : Window
{
    public SavedConnection SelectedConnection { get; private set; } = new();
    public string DatasetName { get; private set; } = "";
    public string Sql { get; private set; } = "";
    public int BatchSize { get; private set; } = 250_000;
    public long? ExpectedRows { get; private set; }

    public ExtractDialog(IEnumerable<SavedConnection> connections, string sql, string datasetName = "database_extract", string? connectionId = null)
    {
        InitializeComponent();
        var list = connections.ToList();
        ConnectionBox.ItemsSource = list;
        ConnectionBox.SelectedItem = list.FirstOrDefault(c => c.Id == connectionId) ?? list.FirstOrDefault();
        DatasetNameText.Text = datasetName;
        SqlText.Text = sql;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionBox.SelectedItem is not SavedConnection connection)
        {
            MessageBox.Show(this, "Select a connection.", "Extract Base Query");
            return;
        }
        if (string.IsNullOrWhiteSpace(DatasetNameText.Text) || string.IsNullOrWhiteSpace(SqlText.Text))
        {
            MessageBox.Show(this, "Dataset name and base query are required.", "Extract Base Query");
            return;
        }
        SelectedConnection = connection;
        DatasetName = DatasetNameText.Text.Trim();
        Sql = SqlText.Text.Trim();
        BatchSize = int.TryParse(BatchSizeText.Text, out var batch) ? Math.Max(1000, batch) : 250_000;
        ExpectedRows = long.TryParse(ExpectedRowsText.Text, out var expected) ? expected : null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
