using System.Windows;

namespace QuerySpace;

public partial class ConnectionDialog : Window
{
    private readonly Workspace _workspace;
    private readonly SavedConnection? _existing;
    public string ConnectionName { get; private set; } = "";
    public string Kind { get; private set; } = "odbc";
    public Dictionary<string, string> Config { get; private set; } = new();
    public string Secret { get; private set; } = "";

    public ConnectionDialog(Workspace workspace, SavedConnection? existing = null)
    {
        InitializeComponent();
        _workspace = workspace;
        _existing = existing;
        if (existing != null) Populate(existing);
    }

    private void Populate(SavedConnection c)
    {
        var cfg = c.Config;
        if (c.Kind == "sqlserver")
        {
            Tabs.SelectedIndex = 1;
            SqlName.Text = c.Name;
            SqlServer.Text = cfg.GetValueOrDefault("server", "");
            SqlDatabase.Text = cfg.GetValueOrDefault("database", "");
            SqlUser.Text = cfg.GetValueOrDefault("user", "");
            SqlTrusted.IsChecked = cfg.GetValueOrDefault("trusted") == "true";
            SqlEncrypt.IsChecked = cfg.GetValueOrDefault("encrypt", "true") != "false";
            SqlTrustCert.IsChecked = cfg.GetValueOrDefault("trustServerCertificate") == "true";
        }
        else if (c.Kind == "snowflake")
        {
            Tabs.SelectedIndex = 2;
            SnowName.Text = c.Name;
            SnowAccount.Text = cfg.GetValueOrDefault("account", "");
            SnowUser.Text = cfg.GetValueOrDefault("user", "");
            SnowWarehouse.Text = cfg.GetValueOrDefault("warehouse", "");
            SnowDatabase.Text = cfg.GetValueOrDefault("database", "");
            SnowSchema.Text = cfg.GetValueOrDefault("schema", "");
            SnowRole.Text = cfg.GetValueOrDefault("role", "");
        }
        else
        {
            Tabs.SelectedIndex = 0;
            OdbcName.Text = c.Name;
            OdbcDsn.Text = cfg.GetValueOrDefault("dsn", "");
            OdbcDatabase.Text = cfg.GetValueOrDefault("database", "");
            OdbcUser.Text = cfg.GetValueOrDefault("user", "");
            OdbcTrusted.IsChecked = cfg.GetValueOrDefault("trusted") == "true";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Capture();
        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            MessageBox.Show(this, "Connection name is required.", "Connection");
            return;
        }
        DialogResult = true;
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Connection test will run through the extraction engine after the first restore/build. Save the connection and run a small base query such as SELECT TOP 10 * FROM table.", "Connection test");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Capture()
    {
        if (Tabs.SelectedIndex == 1)
        {
            ConnectionName = SqlName.Text.Trim();
            Kind = "sqlserver";
            Secret = SqlSecret.Password;
            Config = new()
            {
                ["server"] = SqlServer.Text.Trim(),
                ["database"] = SqlDatabase.Text.Trim(),
                ["user"] = SqlUser.Text.Trim(),
                ["trusted"] = (SqlTrusted.IsChecked == true).ToString().ToLowerInvariant(),
                ["encrypt"] = (SqlEncrypt.IsChecked == true).ToString().ToLowerInvariant(),
                ["trustServerCertificate"] = (SqlTrustCert.IsChecked == true).ToString().ToLowerInvariant()
            };
        }
        else if (Tabs.SelectedIndex == 2)
        {
            ConnectionName = SnowName.Text.Trim();
            Kind = "snowflake";
            Secret = SnowToken.Password;
            Config = new()
            {
                ["account"] = SnowAccount.Text.Trim(),
                ["user"] = SnowUser.Text.Trim(),
                ["warehouse"] = SnowWarehouse.Text.Trim(),
                ["database"] = SnowDatabase.Text.Trim(),
                ["schema"] = SnowSchema.Text.Trim(),
                ["role"] = SnowRole.Text.Trim()
            };
        }
        else
        {
            ConnectionName = OdbcName.Text.Trim();
            Kind = "odbc";
            Secret = OdbcSecret.Password;
            Config = new()
            {
                ["dsn"] = OdbcDsn.Text.Trim(),
                ["database"] = OdbcDatabase.Text.Trim(),
                ["user"] = OdbcUser.Text.Trim(),
                ["trusted"] = (OdbcTrusted.IsChecked == true).ToString().ToLowerInvariant()
            };
        }
    }
}
