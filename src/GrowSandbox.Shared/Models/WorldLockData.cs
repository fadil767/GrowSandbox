namespace GrowSandbox.Shared.Models;

/// <summary>
/// Data otoritatif kepemilikan dan hak akses World Lock untuk sebuah world.
/// Kepemilikan terikat pada <see cref="OwnerAccountId"/> (dengan <see cref="OwnerName"/> untuk tampilan UI)
/// dan daftar akses memakai <see cref="HashSet{T}"/> berisi <c>NormalizedUsername</c> (maksimal 20 pemain, case-insensitive).
/// </summary>
public sealed class WorldLockData
{
    public const int MaxAccessListCount = 20;
    public const int MaxPlayerNameLength = 24;

    public bool IsLocked { get; set; }
    public int OwnerAccountId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public int LockX { get; set; } = -1;
    public int LockY { get; set; } = -1;
    public bool IsPublic { get; set; }
    public HashSet<string> AccessList { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static WorldLockData Unlocked() => new();

    /// <summary>
    /// Memeriksa kepemilikan berdasarkan <see cref="OwnerAccountId"/> jika terikat akun (&gt; 0),
    /// dengan fallback aman ke <see cref="OwnerName"/> hanya untuk data lama/simulasi tanpa AccountId.
    /// </summary>
    public bool IsOwner(int accountId, string? playerName = null)
    {
        if (!IsLocked)
        {
            return false;
        }

        if (OwnerAccountId > 0)
        {
            return accountId > 0 && accountId == OwnerAccountId;
        }

        return IsOwnerByNameFallback(playerName);
    }

    public bool IsOwner(string? playerName)
    {
        if (!IsLocked || OwnerAccountId > 0)
        {
            return false;
        }

        return IsOwnerByNameFallback(playerName);
    }

    private bool IsOwnerByNameFallback(string? playerName) =>
        !string.IsNullOrWhiteSpace(OwnerName) &&
        !string.IsNullOrWhiteSpace(playerName) &&
        string.Equals(OwnerName, playerName.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool HasAccess(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName) || AccessList.Count == 0)
        {
            return false;
        }

        string normalized = NormalizeUsername(playerName);
        return !string.IsNullOrEmpty(normalized) && AccessList.Contains(normalized);
    }

    public bool TryAddAccess(string? playerName, out string normalizedName)
    {
        normalizedName = NormalizeUsername(playerName);
        if (string.IsNullOrEmpty(normalizedName) ||
            string.Equals(OwnerName, normalizedName, StringComparison.OrdinalIgnoreCase) ||
            AccessList.Contains(normalizedName) ||
            AccessList.Count >= MaxAccessListCount)
        {
            return false;
        }

        return AccessList.Add(normalizedName);
    }

    public bool TryRemoveAccess(string? playerName)
    {
        string normalized = NormalizeUsername(playerName);
        return !string.IsNullOrEmpty(normalized) && AccessList.Remove(normalized);
    }

    public void Reset()
    {
        IsLocked = false;
        OwnerAccountId = 0;
        OwnerName = string.Empty;
        LockX = -1;
        LockY = -1;
        IsPublic = false;
        AccessList.Clear();
    }

    public WorldLockData Clone()
    {
        var copy = new WorldLockData
        {
            IsLocked = IsLocked,
            OwnerAccountId = OwnerAccountId,
            OwnerName = OwnerName ?? string.Empty,
            LockX = LockX,
            LockY = LockY,
            IsPublic = IsPublic,
            AccessList = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

        foreach (string raw in AccessList)
        {
            if (copy.AccessList.Count >= MaxAccessListCount)
            {
                break;
            }

            string normalized = NormalizeUsername(raw);
            if (!string.IsNullOrEmpty(normalized))
            {
                copy.AccessList.Add(normalized);
            }
        }

        return copy;
    }

    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        return trimmed.Length > MaxPlayerNameLength ? trimmed[..MaxPlayerNameLength] : trimmed;
    }

    public static string NormalizeUsername(string? name) =>
        SanitizeName(name).ToLowerInvariant();
}
