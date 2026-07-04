using Microsoft.Win32;
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
        WindowTitleBar.Apply(this);
        _workspace = workspace;
        _existing = existing;
        LoadOdbcDsns();
        if (existing != null) Populate(existing);
    }

    private void LoadOdbcDsns()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDsnNames(Registry.CurrentUser.OpenSubKey(@"Software\ODBC\ODBC.INI\ODBC Data Sources"), names);
        AddDsnNames(Registry.LocalMachine.OpenSubKey(@"Software\ODBC\ODBC.INI\ODBC Data Sources"), names);
        foreach (var name in names)
        {
            OdbcDsn.Items.Add(name);
        }
    }

    private static void AddDsnNames(RegistryKey? key, ISet<string> names)
    {
        using (key)
        {
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
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

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Capture();
            if (string.IsNullOrWhiteSpace(ConnectionName))
            {
                MessageBox.Show(this, "Connection name is required before testing.", "Connection test");
                return;
            }

            await DataEngine.TestDatabaseConnectionAsync(Kind, Config, Secret, CancellationToken.None);
            MessageBox.Show(this, "Connection successful.", "Connection test", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (Kind == "sqlserver" && DataEngine.IsSqlServerUntrustedCertificateError(ex))
        {
            var result = MessageBox.Show(
                this,
                DataEngine.SqlServerCertificateHelp + "\n\nDo you want QuerySpace to enable \"Trust server certificate\" for this connection and test again?",
                "SQL Server certificate not trusted",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            SqlTrustCert.IsChecked = true;
            Capture();
            try
            {
                await DataEngine.TestDatabaseConnectionAsync(Kind, Config, Secret, CancellationToken.None);
                MessageBox.Show(this, "Connection successful. Click Save to keep the updated certificate setting.", "Connection test", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception retryEx)
            {
                MessageBox.Show(this, retryEx.Message, "Connection test failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            var detail = Kind == "sqlserver"
                ? ex.Message + "\n\n" + DataEngine.SqlServerConnectionDiagnostic(Config)
                : ex.Message;
            MessageBox.Show(this, detail, "Connection test failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
