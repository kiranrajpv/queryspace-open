namespace QuerySpace;

public sealed class AiService : IAsyncDisposable
{
    private readonly AiSettingsStore _settingsStore;
    private readonly FoundryLocalService _localModels;
    private readonly CloudAgentService _cloudAgent;
    private readonly AgentFrameworkService _agentFramework = new();

    public AiService(AiSettingsStore settingsStore, FoundryLocalService localModels)
    {
        _settingsStore = settingsStore;
        _localModels = localModels;
        _cloudAgent = new CloudAgentService(settingsStore);
    }

    public async Task<IReadOnlyList<AiModelOption>> GetAvailableModelsAsync(CancellationToken token)
    {
        var result = new List<AiModelOption>();
        try
        {
            var local = await _localModels.GetModelsAsync(cachedOnly: true, token);
            result.AddRange(local.Select(model => new AiModelOption
            {
                Key = "local:" + model.Alias,
                DisplayName = model.DisplayName,
                Provider = "Foundry Local",
                ModelId = model.Alias,
                IsLocal = true,
                SupportsTools = model.SupportsTools
            }));
        }
        catch
        {
            // Cloud profiles remain usable if the local runtime or catalog is unavailable.
        }

        var settings = _settingsStore.Load();
        result.AddRange(settings.CloudProfiles.Where(p => p.Enabled).Select(profile => new AiModelOption
        {
            Key = "cloud:" + profile.Id,
            DisplayName = profile.Name,
            Provider = profile.Provider,
            ModelId = profile.Model,
            ProfileId = profile.Id,
            IsLocal = false,
            SupportsTools = profile.SupportsTools
        }));
        return result.OrderByDescending(m => m.IsLocal).ThenBy(m => m.DisplayName).ToList();
    }

    public async Task<AgentRunResult> RunAsync(
        AiModelOption model,
        string prompt,
        AgentToolService tools,
        IProgress<string>? activity,
        IProgress<string>? streamedText,
        CancellationToken token)
    {
        if (!model.SupportsTools)
            throw new InvalidOperationException($"'{model.DisplayName}' is marked Chat only. Select a model with Agent tools capability.");

        tools.BeginAgentRun();
        activity?.Report("Sending the request and available tools to the model...");

        if (model.IsLocal)
            return await _agentFramework.RunAsync(
                model.Key,
                () => _localModels.CreateChatClient(model.ModelId, activity),
                prompt,
                tools,
                activity,
                streamedText,
                token);

        var profile = _settingsStore.Load().CloudProfiles.SingleOrDefault(p => p.Id == model.ProfileId)
            ?? throw new InvalidOperationException("The selected cloud model profile no longer exists.");
        return await _cloudAgent.RunAsync(profile, prompt, tools, activity, streamedText, token);
    }

    public ValueTask DisposeAsync() => _localModels.DisposeAsync();
}
