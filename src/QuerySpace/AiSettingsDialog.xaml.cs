using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace QuerySpace;

public partial class AiSettingsDialog : Window
{
    private readonly AiSettingsStore _store;
    private readonly FoundryLocalService _localModels;
    private readonly ObservableCollection<LocalModelInfo> _models = new();
    private readonly ObservableCollection<CloudModelProfile> _profiles = new();
    private AiSettings _settings;

    public AiSettingsDialog(AiSettingsStore store, FoundryLocalService localModels)
    {
        InitializeComponent();
        WindowTitleBar.Apply(this);
        _store = store;
        _localModels = localModels;
        _settings = store.Load();
        LocalModelsGrid.ItemsSource = _models;
        DetectedHardwareText.Text = HardwareDetectionService.GpuSummary();
        ProfilesList.ItemsSource = _profiles;
        foreach (var profile in _settings.CloudProfiles) _profiles.Add(profile);
        LoadMcpSettings();
        if (_profiles.Count > 0) ProfilesList.SelectedIndex = 0;
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshModelsAsync()
    {
        DetectedHardwareText.Text = HardwareDetectionService.GpuSummary();
        SetModelBusy(true, "Loading the hardware-compatible model catalog...");
        try
        {
            var models = await _localModels.GetModelsAsync(cachedOnly: false, CancellationToken.None);
            _models.Clear();
            foreach (var model in models) _models.Add(model);
            var providers = await _localModels.GetExecutionProviderStatusAsync(CancellationToken.None);
            LocalModelStatus.Text = $"{models.Count} compatible chat models. {providers}";
        }
        catch (Exception ex)
        {
            LocalModelStatus.Text = "Model catalog could not be loaded.";
            MessageBox.Show(this, ex.Message, "Local model catalog", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetModelBusy(false); }
    }

    private async void RedetectHardware_Click(object sender, RoutedEventArgs e)
    {
        SetModelBusy(true, "Restarting the local AI runtime and detecting graphics adapters...");
        try
        {
            await _localModels.RedetectHardwareAsync(CancellationToken.None);
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GPU detection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetModelBusy(false); }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (LocalModelsGrid.SelectedItem is not LocalModelInfo model) return;
        var license = string.IsNullOrWhiteSpace(model.LicenseDescription) ? model.License : model.LicenseDescription;
        var answer = MessageBox.Show(this,
            $"Download {model.DisplayName} ({model.Size})?\n\nPublisher: {model.Publisher}\nLicense: {model.License}\n\n{license}\n\nReview and accept the publisher's license before continuing.",
            "Download local model", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SetModelBusy(true, $"Downloading {model.DisplayName}...");
        LocalModelProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(value =>
        {
            LocalModelProgress.Value = value;
            LocalModelStatus.Text = $"Downloading {model.DisplayName}: {value:F1}%";
        });
        try
        {
            await _localModels.DownloadAsync(model.Alias, progress, CancellationToken.None);
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Model download failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LocalModelProgress.Visibility = Visibility.Collapsed;
            SetModelBusy(false);
        }
    }

    private async void EnableGpu_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "QuerySpace will download and register the hardware execution providers Foundry Local reports as compatible with this computer. Provider components can have separate vendor license terms. Continue?",
            "Enable local GPU acceleration", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SetModelBusy(true, "Preparing GPU execution providers...");
        LocalModelProgress.IsIndeterminate = false;
        LocalModelProgress.Visibility = Visibility.Visible;
        var progress = new Progress<(string Provider, double Percent)>(value =>
        {
            LocalModelProgress.Value = value.Percent;
            LocalModelStatus.Text = $"Preparing {value.Provider}: {value.Percent:F1}%";
        });
        try
        {
            await _localModels.EnableGpuAccelerationAsync(progress, CancellationToken.None);
            await _localModels.RedetectHardwareAsync(CancellationToken.None);
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GPU acceleration setup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LocalModelProgress.Visibility = Visibility.Collapsed;
            SetModelBusy(false);
        }
    }

    private async void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (LocalModelsGrid.SelectedItem is not LocalModelInfo { IsCached: true } model) return;
        if (MessageBox.Show(this, $"Remove the downloaded model {model.DisplayName}?", "Remove local model", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetModelBusy(true, $"Removing {model.DisplayName}...");
        try
        {
            await _localModels.RemoveAsync(model.Alias, CancellationToken.None);
            await RefreshModelsAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Remove model failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetModelBusy(false); }
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new CloudModelProfile { Name = "New cloud model" };
        _profiles.Add(profile);
        ProfilesList.SelectedItem = profile;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not CloudModelProfile profile) return;
        if (MessageBox.Show(this, $"Delete cloud model profile '{profile.Name}'?", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        _store.DeleteSecret(profile.Id);
        ClearProfileForm();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not CloudModelProfile profile) return;
        ProfileNameText.Text = profile.Name;
        SelectProvider(profile.Provider);
        EndpointText.Text = profile.Endpoint;
        ModelText.Text = profile.Model;
        ApiVersionText.Text = profile.ApiVersion;
        ApiKeyBox.Password = "";
        ProfileEnabledCheck.IsChecked = profile.Enabled;
        ProfileToolsCheck.IsChecked = profile.SupportsTools;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not CloudModelProfile profile)
        {
            MessageBox.Show(this, "Add or select a cloud model profile first.", "Cloud models");
            return;
        }
        ApplyProfileForm(profile);
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _store.SaveSecret(profile.Id, ApiKeyBox.Password);
        SaveAll();
        ProfilesList.Items.Refresh();
        ApiKeyBox.Clear();
        MessageBox.Show(this, "Cloud model profile saved. The API key is encrypted for the current Windows user.", "Cloud models");
    }

    private async void TestProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not CloudModelProfile profile) return;
        ApplyProfileForm(profile);
        var secret = string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? _store.GetSecret(profile.Id) : ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(secret) &&
            !profile.Provider.Equals("OpenAI Compatible", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Enter an API key or save one for this profile.", "Test connection");
            return;
        }
        try
        {
            IsEnabled = false;
            await new CloudAgentService(_store).TestAsync(profile, secret, CancellationToken.None);
            MessageBox.Show(this, "The model connection completed successfully.", "Test connection", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Test connection failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is CloudModelProfile profile)
        {
            ApplyProfileForm(profile);
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _store.SaveSecret(profile.Id, ApiKeyBox.Password);
        }
        SaveAll();
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveAll()
    {
        _settings.McpEnabled = McpEnabledCheck.IsChecked == true;
        _settings.AllowDatasetRead = AllowDatasetReadCheck.IsChecked == true;
        _settings.AllowEditorRead = AllowEditorReadCheck.IsChecked == true;
        _settings.AllowEditorWrite = AllowEditorWriteCheck.IsChecked == true;
        _settings.AllowSqlExecution = AllowExecutionCheck.IsChecked == true;
        _settings.RequireSqlApproval = RequireApprovalCheck.IsChecked == true;
        _settings.AuditToolCalls = AuditCheck.IsChecked == true;
        _settings.MaximumToolRows = Math.Clamp(ParseInt(MaximumRowsText.Text, 200), 1, 500);
        _settings.QueryTimeoutSeconds = Math.Clamp(ParseInt(QueryTimeoutText.Text, 60), 5, 600);
        _settings.CloudProfiles = _profiles.ToList();
        _store.Save(_settings);
    }

    private void LoadMcpSettings()
    {
        McpEnabledCheck.IsChecked = _settings.McpEnabled;
        AllowDatasetReadCheck.IsChecked = _settings.AllowDatasetRead;
        AllowEditorReadCheck.IsChecked = _settings.AllowEditorRead;
        AllowEditorWriteCheck.IsChecked = _settings.AllowEditorWrite;
        AllowExecutionCheck.IsChecked = _settings.AllowSqlExecution;
        RequireApprovalCheck.IsChecked = _settings.RequireSqlApproval;
        AuditCheck.IsChecked = _settings.AuditToolCalls;
        MaximumRowsText.Text = _settings.MaximumToolRows.ToString();
        QueryTimeoutText.Text = _settings.QueryTimeoutSeconds.ToString();
    }

    private void ApplyProfileForm(CloudModelProfile profile)
    {
        profile.Name = string.IsNullOrWhiteSpace(ProfileNameText.Text) ? "Cloud model" : ProfileNameText.Text.Trim();
        profile.Provider = (ProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "OpenAI";
        profile.Endpoint = EndpointText.Text.Trim();
        profile.Model = ModelText.Text.Trim();
        profile.ApiVersion = ApiVersionText.Text.Trim();
        profile.Enabled = ProfileEnabledCheck.IsChecked == true;
        profile.SupportsTools = ProfileToolsCheck.IsChecked == true;
    }

    private void SelectProvider(string provider)
    {
        ProviderCombo.SelectedItem = ProviderCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), provider, StringComparison.OrdinalIgnoreCase));
        ProviderCombo.SelectedIndex = Math.Max(0, ProviderCombo.SelectedIndex);
    }

    private void ClearProfileForm()
    {
        ProfileNameText.Clear(); EndpointText.Clear(); ModelText.Clear(); ApiVersionText.Clear(); ApiKeyBox.Clear();
        ProfileEnabledCheck.IsChecked = false; ProfileToolsCheck.IsChecked = false; ProviderCombo.SelectedIndex = 0;
    }

    private void SetModelBusy(bool busy, string? status = null)
    {
        DownloadModelButton.IsEnabled = !busy;
        RemoveModelButton.IsEnabled = !busy;
        EnableGpuButton.IsEnabled = !busy;
        LocalModelsGrid.IsEnabled = !busy;
        if (status != null) LocalModelStatus.Text = status;
    }

    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
}
