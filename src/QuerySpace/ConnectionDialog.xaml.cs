using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

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

    private void OdbcHelp_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "HANA ODBC Help",
            Width = 820,
            Height = 620,
            MinWidth = 720,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };
        WindowTitleBar.Apply(window);

        var root = new DockPanel { Margin = new Thickness(16) };
        var close = new Button
        {
            Content = "Close",
            MinWidth = 96,
            MinHeight = 32,
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var linkPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        AddLinkButton(linkPanel, "Download HANA tools", "https://tools.hana.ondemand.com/#hanatools");
        AddLinkButton(linkPanel, "Download Windows x64 client ZIP", "https://tools.hana.ondemand.com/additional/hanaclient-latest-windows-x64.zip");
        AddLinkButton(linkPanel, "Install client tools guide", "https://help.sap.com/docs/SAP_HANA_CLIENT/8e208b44c0784f028b948958ef1d05e7/c5d4a5c3bb57101486b683177bee7725.html?locale=en-US&version=LATEST");
        AddLinkButton(linkPanel, "Configure HANA ODBC guide", "https://help.sap.com/docs/SAP_PREDICTIVE_ANALYTICS/e9146b36040844d0b1f309bc8c1ba6ab/734759c0c1c9440c857da0d366e47dda.html?version=LATEST&locale=en-US");
        DockPanel.SetDock(linkPanel, Dock.Top);
        root.Children.Add(linkPanel);

        var helpText = new TextBox
        {
            Text = HanaOdbcHelpText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(10)
        };
        root.Children.Add(helpText);
        window.Content = root;
        window.ShowDialog();
    }

    private static void AddLinkButton(Panel panel, string label, string url)
    {
        var button = new Button
        {
            Content = label,
            Tag = url,
            MinHeight = 32,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10, 5, 10, 5)
        };
        button.Click += (_, _) => OpenUrl(url);
        panel.Children.Add(button);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private const string HanaOdbcHelpText = """
HANA Connections are supported using ODBC.

Use these steps on the Windows machine where QuerySpace is installed.

1. Download ODBC Driver for HANA (HANA Client Tools)

   SAP HANA tools page:
   https://tools.hana.ondemand.com/#hanatools

   Direct Windows x64 HANA Client ZIP:
   https://tools.hana.ondemand.com/additional/hanaclient-latest-windows-x64.zip

2. Install Client Tools

   SAP installation guide:
   https://help.sap.com/docs/SAP_HANA_CLIENT/8e208b44c0784f028b948958ef1d05e7/c5d4a5c3bb57101486b683177bee7725.html?locale=en-US&version=LATEST

   Typical steps:
   - Extract hanaclient-latest-windows-x64.zip.
   - Run hdbsetup.exe or hdbinst.exe from the extracted client folder.
   - Install the 64-bit SAP HANA Client.
   - Confirm the HANA ODBC driver is available after install.

3. Configure HANA ODBC Connection

   SAP ODBC configuration guide:
   https://help.sap.com/docs/SAP_PREDICTIVE_ANALYTICS/e9146b36040844d0b1f309bc8c1ba6ab/734759c0c1c9440c857da0d366e47dda.html?version=LATEST&locale=en-US

   Windows steps:
   - Open 64-bit ODBC Data Source Administrator:
     C:\Windows\System32\odbcad32.exe

   - Use User DSN or System DSN.
   - Click Add.
   - Select the SAP HANA ODBC driver, usually named HDBODBC.
   - Create a DSN name, for example HANA_DEV.
   - Enter the HANA server host and SQL port.

   Common HANA SQL port format:
   - Instance 00: 30015
   - Instance 01: 30115
   - Instance 02: 30215

   For SAP HANA Cloud, use the host and port shown in the SAP HANA Cloud connection details.

4. Add the connection in QuerySpace

   - Open Add Connection.
   - Select ODBC DSN.
   - Select or type the DSN name, for example HANA_DEV.
   - Enter user and password/token if required.
   - Click Test connection.
   - Click Save.

5. Smoke-test query

   Use Extract Base Query and test:

   select current_timestamp as hana_time
   from dummy;

   Or:

   select current_schema as current_schema
   from dummy;

Notes:
- QuerySpace is a 64-bit application, so configure the DSN in the 64-bit ODBC Administrator.
- C:\Windows\System32\odbcad32.exe is the 64-bit ODBC Administrator.
- C:\Windows\SysWOW64\odbcad32.exe is the 32-bit ODBC Administrator and should not be used for QuerySpace.
- If your company requires SSL/TLS for HANA, configure it in the SAP HANA ODBC DSN/driver settings.
""";

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
