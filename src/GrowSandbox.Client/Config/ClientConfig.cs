using System.Text.Json;
using GrowSandbox.Shared.Networking;

namespace GrowSandbox.Client.Config;

/// <summary>
/// Konfigurasi lokal ringan client (<c>client_config.json</c>).
/// Hanya menyimpan <see cref="LastUsername"/> dan <see cref="LastServerAddress"/>;
/// password TIDAK PERNAH disimpan ke disk.
/// </summary>
public sealed class ClientConfig
{
    public const string DefaultServerAddress = "stallion-hills.tun.ply.gg:11872";
    private const string ConfigFileName = "client_config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string LastUsername { get; set; } = string.Empty;
    public string LastServerAddress { get; set; } = DefaultServerAddress;

    private static string GetConfigPath() =>
        Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);

    public static ClientConfig Load()
    {
        try
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                return new ClientConfig();
            }

            string json = File.ReadAllText(path);
            ClientConfig? loaded = JsonSerializer.Deserialize<ClientConfig>(json);
            if (loaded is null)
            {
                return new ClientConfig();
            }

            loaded.LastUsername = (loaded.LastUsername ?? string.Empty).Trim();
            loaded.LastServerAddress = string.IsNullOrWhiteSpace(loaded.LastServerAddress)
                ? DefaultServerAddress
                : loaded.LastServerAddress.Trim();
            return loaded;
        }
        catch
        {
            return new ClientConfig();
        }
    }

    public void Save()
    {
        try
        {
            var safeSnapshot = new ClientConfig
            {
                LastUsername = (LastUsername ?? string.Empty).Trim(),
                LastServerAddress = string.IsNullOrWhiteSpace(LastServerAddress)
                    ? DefaultServerAddress
                    : LastServerAddress.Trim(),
            };

            string json = JsonSerializer.Serialize(safeSnapshot, JsonOptions);
            File.WriteAllText(GetConfigPath(), json);
        }
        catch
        {
            // Abaikan error tulis file agar tidak mengganggu gameplay.
        }
    }

    public static bool TryParseServerAddress(string? rawAddress, out string host, out int port)
    {
        host = string.Empty;
        port = NetworkConstants.DefaultPort;

        string trimmed = (rawAddress ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        int colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < trimmed.Length - 1)
        {
            string hostPart = trimmed[..colonIdx].Trim();
            string portPart = trimmed[(colonIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(hostPart) || !int.TryParse(portPart, out int parsedPort) || parsedPort is < 1 or > 65535)
            {
                return false;
            }

            host = hostPart;
            port = parsedPort;
            return true;
        }

        if (trimmed.Contains(':'))
        {
            return false;
        }

        host = trimmed;
        port = NetworkConstants.DefaultPort;
        return true;
    }
}
