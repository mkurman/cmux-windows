using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cmux.Core.Services;

/// <summary>
/// Stores secrets encrypted with Windows DPAPI in %LOCALAPPDATA%/cmux/secrets.json.
/// DPAPI provides both confidentiality and integrity — no separate MAC is needed.
/// </summary>
public static class SecretStoreService
{
    private static readonly string SecretsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");

    private static readonly string SecretsPath =
        Path.Combine(SecretsDir, "secrets.json");

    // Additional entropy for DPAPI to bind ciphertext to this application
    private static readonly byte[] Entropy = "cmux-secret-store-v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string? GetSecret(string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            return null;

        try
        {
            var map = LoadRawSecrets();
            if (!map.TryGetValue(secretName, out var encoded) || string.IsNullOrWhiteSpace(encoded))
                return null;

            var blob = Convert.FromBase64String(encoded);

            // Try current format: DPAPI with entropy
            try
            {
                var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException)
            {
                // Fall through to legacy formats
            }

            // Try previous format: HMAC(32) || ciphertext (strip the HMAC prefix)
            if (blob.Length > 32)
            {
                try
                {
                    var ciphertext = blob[32..];
                    var plain = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
                catch (CryptographicException)
                {
                    // Fall through to oldest format
                }
            }

            // Oldest format: DPAPI without entropy
            var legacyPlain = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(legacyPlain);
        }
        catch
        {
            return null;
        }
    }

    public static void SetSecret(string secretName, string? value)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            return;

        try
        {
            var map = LoadRawSecrets();

            if (string.IsNullOrWhiteSpace(value))
            {
                map.Remove(secretName);
            }
            else
            {
                var plain = Encoding.UTF8.GetBytes(value);
                var ciphertext = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                map[secretName] = Convert.ToBase64String(ciphertext);
            }

            SaveRawSecrets(map);
        }
        catch
        {
            // Ignore secret persistence errors to avoid crashing the app.
        }
    }

    public static void RemoveSecret(string secretName)
    {
        SetSecret(secretName, null);
    }

    private static Dictionary<string, string> LoadRawSecrets()
    {
        try
        {
            if (!File.Exists(SecretsPath))
                return [];

            var json = File.ReadAllText(SecretsPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveRawSecrets(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(SecretsDir);

        var tmp = SecretsPath + ".tmp";
        var json = JsonSerializer.Serialize(map, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, SecretsPath, overwrite: true);
    }
}
