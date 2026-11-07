using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuerySpace;

public sealed class AiSettingsStore
{
    private const string AppName = "QuerySpace";
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();

    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string ModelsFolder => Path.Combine(AppDataFolder, "models");
    public static string AuditFolder => Path.Combine(AppDataFolder, "audit");
    private static string SettingsPath => Path.Combine(AppDataFolder, "ai-settings.json");
    private static string SecretsFolder => Path.Combine(AppDataFolder, ".ai-secrets");

    public AiSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new AiSettings();
                return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(SettingsPath), _jsonOptions) ?? new AiSettings();
            }
            catch
            {
                return new AiSettings();
            }
        }
    }

    public void Save(AiSettings settings)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(AppDataFolder);
            Workspace.WriteTextAtomically(SettingsPath, JsonSerializer.Serialize(settings, _jsonOptions));
        }
    }

    public void SaveSecret(string profileId, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_sync)
        {
            Directory.CreateDirectory(SecretsFolder);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(SecretPath(profileId), protectedBytes);
        }
    }

    public string GetSecret(string profileId)
    {
        lock (_sync)
        {
            try
            {
                var path = SecretPath(profileId);
                if (!File.Exists(path)) return "";
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }
    }

    public void DeleteSecret(string profileId)
    {
        lock (_sync)
        {
            var path = SecretPath(profileId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public void Audit(string operation, object details)
    {
        var settings = Load();
        if (!settings.AuditToolCalls) return;
        lock (_sync)
        {
            Directory.CreateDirectory(AuditFolder);
            var record = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                operation,
                details
            });
            File.AppendAllText(Path.Combine(AuditFolder, $"ai-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"), record + Environment.NewLine);
        }
    }

    private static string SecretPath(string profileId) => Path.Combine(SecretsFolder, Workspace.SafeName(profileId) + ".bin");
}
