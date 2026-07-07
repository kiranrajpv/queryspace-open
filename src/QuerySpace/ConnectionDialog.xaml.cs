using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

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

    private string CapturedSecretOrExisting()
    {
        var captured = Tabs.SelectedIndex switch
        {
            1 => SqlSecret.Password,
            2 => SnowToken.Password,
            _ => OdbcSecret.Password
        };

        if (!string.IsNullOrEmpty(captured) || _existing == null)
        {
            return captured;
        }

        return _workspace.GetSecret(_existing.Id);
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
            SetComboBoxByTag(SnowAuthType, cfg.GetValueOrDefault("authType", "password"));
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
            var detail = Kind switch
            {
                "sqlserver" => ex.Message + "\n\n" + DataEngine.SqlServerConnectionDiagnostic(Config),
                "snowflake" => ex.Message + "\n\n" + DataEngine.SnowflakeConnectionDiagnostic(Config),
                _ => ex.Message
            };
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

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var helpText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Padding = new Thickness(10)
        };
        AddHanaOdbcHelpContent(helpText);
        scrollViewer.Content = helpText;
        root.Children.Add(scrollViewer);
        window.Content = root;
        window.ShowDialog();
    }

    private static void AddHanaOdbcHelpContent(TextBlock text)
    {
        AddText(text, "HANA Connections are supported using ODBC.\n\nUse these steps on the Windows machine where QuerySpace is installed.\n\n");
        AddStepHeading(text, "Step 1: Download ODBC Driver for HANA (HANA Client Tools)");
        AddText(text, "\nSAP HANA tools page:\n");
        AddHyperlink(text, "https://tools.hana.ondemand.com/#hanatools");
        AddText(text, "\n\nDirect Windows x64 HANA Client ZIP:\n");
        AddHyperlink(text, "https://tools.hana.ondemand.com/additional/hanaclient-latest-windows-x64.zip");
        AddText(text, "\n\n");
        AddStepHeading(text, "Step 2: Install Client Tools");
        AddText(text, "\nSAP installation guide:\n");
        AddHyperlink(text, "https://help.sap.com/docs/SAP_HANA_CLIENT/8e208b44c0784f028b948958ef1d05e7/c5d4a5c3bb57101486b683177bee7725.html?locale=en-US&version=LATEST");
        AddText(text, "\n\nTypical steps:\n- Extract hanaclient-latest-windows-x64.zip.\n- Run hdbsetup.exe or hdbinst.exe from the extracted client folder.\n- Install the 64-bit SAP HANA Client.\n- Confirm the HANA ODBC driver is available after install.\n\n");
        AddStepHeading(text, "Step 3: Configure HANA ODBC Connection");
        AddText(text, "\nSAP ODBC configuration guide:\n");
        AddHyperlink(text, "https://help.sap.com/docs/SAP_PREDICTIVE_ANALYTICS/e9146b36040844d0b1f309bc8c1ba6ab/734759c0c1c9440c857da0d366e47dda.html?version=LATEST&locale=en-US");
        AddText(text, "\n\nWindows steps:\n- Open 64-bit ODBC Data Source Administrator:\n  C:\\Windows\\System32\\odbcad32.exe\n\n- Use User DSN or System DSN.\n- Click Add.\n- Select the SAP HANA ODBC driver, usually named HDBODBC.\n- Create a DSN name, for example HANA_DEV.\n- Enter the HANA server host and SQL port.\n\nCommon HANA SQL port format:\n- Instance 00: 30015\n- Instance 01: 30115\n- Instance 02: 30215\n\nFor SAP HANA Cloud, use the host and port shown in the SAP HANA Cloud connection details.\n\n");
        AddStepHeading(text, "Step 4: Add the connection in QuerySpace");
        AddText(text, "\n- Open Add Connection.\n- Select ODBC DSN.\n- Select or type the DSN name, for example HANA_DEV.\n- Enter user and password/token if required.\n- Click Test connection.\n- Click Save.\n\n");
        AddStepHeading(text, "Step 5: Smoke-test the HANA connection");
        AddText(text, "\nUse Extract Base Query and test:\n\nselect current_timestamp as hana_time\nfrom dummy;\n\nOr:\n\nselect current_schema as current_schema\nfrom dummy;\n\n");
        AddStepHeading(text, "Notes");
        AddText(text, "\n");
        AddText(text, "- QuerySpace is a 64-bit application, so configure the DSN in the 64-bit ODBC Administrator.\n- C:\\Windows\\System32\\odbcad32.exe is the 64-bit ODBC Administrator.\n- C:\\Windows\\SysWOW64\\odbcad32.exe is the 32-bit ODBC Administrator and should not be used for QuerySpace.\n- If your company requires SSL/TLS for HANA, configure it in the SAP HANA ODBC DSN/driver settings.\n");
    }

    private static void AddStepHeading(TextBlock text, string value)
    {
        text.Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold });
    }

    private static void AddText(TextBlock text, string value)
    {
        text.Inlines.Add(new Run(value));
    }

    private static void AddHyperlink(TextBlock text, string url)
    {
        var hyperlink = new Hyperlink(new Run(url))
        {
            NavigateUri = new Uri(url)
        };
        hyperlink.RequestNavigate += (_, args) => OpenUrl(args.Uri.AbsoluteUri);
        text.Inlines.Add(hyperlink);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Capture()
    {
        if (Tabs.SelectedIndex == 1)
        {
            ConnectionName = SqlName.Text.Trim();
            Kind = "sqlserver";
            Secret = CapturedSecretOrExisting();
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
            Secret = CapturedSecretOrExisting();
            Config = new()
            {
                ["account"] = SnowAccount.Text.Trim(),
                ["user"] = SnowUser.Text.Trim(),
                ["authType"] = SelectedComboBoxTag(SnowAuthType, "password"),
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
            Secret = CapturedSecretOrExisting();
            Config = new()
            {
                ["dsn"] = OdbcDsn.Text.Trim(),
                ["database"] = OdbcDatabase.Text.Trim(),
                ["user"] = OdbcUser.Text.Trim(),
                ["trusted"] = (OdbcTrusted.IsChecked == true).ToString().ToLowerInvariant()
            };
        }
    }

    private static string SelectedComboBoxTag(ComboBox comboBox, string defaultValue)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : defaultValue;
    }

    private static void SetComboBoxByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string itemTag && itemTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
        comboBox.SelectedIndex = 0;
    }
}
