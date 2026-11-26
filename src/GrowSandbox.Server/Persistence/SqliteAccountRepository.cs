using System.Buffers.Binary;
using GrowSandbox.Server.Security;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Server.Persistence;

public sealed record AccountRecord(
    int AccountId,
    string Username,
    string NormalizedUsername,
    int Gems,
    byte[]? InventoryBlob,
    string LastWorld,
    long CreatedUtcTicks,
    long LastLoginUtcTicks);

public readonly record struct AccountAuthResult(
    bool Success,
    AccountRecord? Account,
    string ErrorMessage);

public readonly record struct AccountProfileSaveEntry(
    int AccountId,
    int Gems,
    byte[]? InventoryBlob,
    string LastWorld);

public interface IAccountRepository
{
    AccountAuthResult Register(
        string username,
        string password,
        byte[]? initialInventoryBlob = null,
        int initialGems = 0);

    AccountAuthResult Authenticate(string username, string password);
    AccountRecord? GetById(int accountId);
    AccountRecord? GetByUsername(string username);
    void SaveProgress(int accountId, int gems, byte[]? inventoryBlob, string lastWorld);
    void SaveTradeProfilesAtomic(AccountProfileSaveEntry accountA, AccountProfileSaveEntry accountB);
    Task SaveTradeProfilesAtomicAsync(
        AccountProfileSaveEntry accountA,
        AccountProfileSaveEntry accountB,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Codec biner ringkas untuk menyimpan dan memuat <c>Dictionary&lt;ushort, ushort&gt;</c>
/// milik akun pada kolom <c>Accounts.InventoryBlob</c> (tidak menyertakan Fist/Wrench/Gem).
/// </summary>
public static class AccountInventoryCodec
{
    public static byte[] Encode(IReadOnlyDictionary<ushort, ushort> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var validEntries = new List<KeyValuePair<ushort, ushort>>(inventory.Count);
        foreach (KeyValuePair<ushort, ushort> kvp in inventory)
        {
            if (IsPersistableInventoryItem(kvp.Key) && kvp.Value > 0)
            {
                validEntries.Add(new KeyValuePair<ushort, ushort>(
                    kvp.Key,
                    Math.Min(kvp.Value, ItemCatalog.MaxInventoryStack)));
            }
        }

        var buffer = new byte[4 + validEntries.Count * 4];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), validEntries.Count);

        int offset = 4;
        for (int i = 0; i < validEntries.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), validEntries[i].Key);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), validEntries[i].Value);
            offset += 4;
        }

        return buffer;
    }

    public static Dictionary<ushort, ushort>? Decode(byte[]? blob)
    {
        if (blob is null || blob.Length < 4)
        {
            return null;
        }

        ReadOnlySpan<byte> span = blob;
        int count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, 4));
        if (count < 0 || count > PacketSerializer.MaxInventorySlots || 4 + (count * 4) > blob.Length)
        {
            return null;
        }

        var result = new Dictionary<ushort, ushort>(count);
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            ushort itemId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
            ushort amount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 2, 2));
            offset += 4;

            if (IsPersistableInventoryItem(itemId) && amount > 0)
            {
                result[itemId] = Math.Min(amount, ItemCatalog.MaxInventoryStack);
            }
        }

        return result;
    }

    private static bool IsPersistableInventoryItem(ushort itemId) =>
        itemId is not (TileIds.Air or TileIds.Gem or TileIds.Fist or TileIds.Wrench);
}

/// <summary>
/// Repositori akun SQLite pada database yang sama (<c>growsandbox.db</c>) dengan mode WAL aktif.
/// Menyimpan hash PBKDF2-SHA256 &amp; salt 16 byte tanpa pernah menyimpan atau mencatat plaintext password.
/// </summary>
public sealed class SqliteAccountRepository : IAccountRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly bool _isInMemory;
    private readonly SqliteConnection? _inMemoryAnchorConnection;
    private readonly object _inMemoryLock = new();

    public string DatabasePath { get; }

    public SqliteAccountRepository(string databasePath = "growsandbox.db")
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Path database SQLite tidak boleh kosong.", nameof(databasePath));
        }

        DatabasePath = databasePath;
        _isInMemory = string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connectionString = builder.ToString();
        if (_isInMemory)
        {
            _inMemoryAnchorConnection = new SqliteConnection(_connectionString);
            _inMemoryAnchorConnection.Open();
        }

        EnsureSchema();
    }

    public static bool TryValidateRegistrationInput(
        string? rawUsername,
        string? password,
        out string sanitizedUsername,
        out string errorMessage)
    {
        sanitizedUsername = (rawUsername ?? string.Empty).Trim();

        if (sanitizedUsername.Length < NetworkConstants.MinUsernameLength ||
            sanitizedUsername.Length > NetworkConstants.MaxUsernameLength)
        {
            errorMessage = $"Username harus {NetworkConstants.MinUsernameLength}-{NetworkConstants.MaxUsernameLength} karakter.";
            return false;
        }

        for (int i = 0; i < sanitizedUsername.Length; i++)
        {
            char c = sanitizedUsername[i];
            bool validChar =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_';
            if (!validChar)
            {
                errorMessage = "Username hanya boleh berisi huruf, angka, dan underscore (_).";
                return false;
            }
        }

        if (string.IsNullOrEmpty(password) || password.Length < NetworkConstants.MinPasswordLength)
        {
            errorMessage = $"Password minimal {NetworkConstants.MinPasswordLength} karakter.";
            return false;
        }

        if (password.Length > NetworkConstants.MaxPasswordLength)
        {
            errorMessage = $"Password maksimal {NetworkConstants.MaxPasswordLength} karakter.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private void EnsureSchema()
    {
        ExecuteWithConnection(connection =>
        {
            if (!_isInMemory)
            {
                using var walCmd = connection.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                walCmd.ExecuteNonQuery();
            }

            using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Accounts (
                    AccountId INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL,
                    NormalizedUsername TEXT NOT NULL UNIQUE,
                    PasswordHash BLOB NOT NULL,
                    PasswordSalt BLOB NOT NULL,
                    Gems INTEGER NOT NULL DEFAULT 0,
                    InventoryBlob BLOB,
                    LastWorld TEXT NOT NULL DEFAULT 'START',
                    CreatedUtcTicks INTEGER NOT NULL,
                    LastLoginUtcTicks INTEGER NOT NULL
                );
                """;
            schemaCmd.ExecuteNonQuery();
        });
    }

    public AccountAuthResult Register(
        string username,
        string password,
        byte[]? initialInventoryBlob = null,
        int initialGems = 0)
    {
        if (!TryValidateRegistrationInput(username, password, out string sanitizedUsername, out string validationError))
        {
            return new AccountAuthResult(false, null, validationError);
        }

        string normalized = sanitizedUsername.ToLowerInvariant();
        (byte[] hash, byte[] salt) = PasswordHasher.HashPassword(password);
        long nowTicks = DateTime.UtcNow.Ticks;
        int safeGems = Math.Clamp(initialGems, 0, ItemCatalog.MaxGemBalance);
        const string defaultWorld = "START";

        return ExecuteWithConnection(connection =>
        {
            try
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText =
                    """
                    INSERT INTO Accounts (
                        Username,
                        NormalizedUsername,
                        PasswordHash,
                        PasswordSalt,
                        Gems,
                        InventoryBlob,
                        LastWorld,
                        CreatedUtcTicks,
                        LastLoginUtcTicks
                    )
                    VALUES (
                        $username,
                        $normalized,
                        $hash,
                        $salt,
                        $gems,
                        $inventoryBlob,
                        $lastWorld,
                        $createdTicks,
                        $lastLoginTicks
                    );
                    SELECT last_insert_rowid();
                    """;
                insertCmd.Parameters.AddWithValue("$username", sanitizedUsername);
                insertCmd.Parameters.AddWithValue("$normalized", normalized);
                insertCmd.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
                insertCmd.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
                insertCmd.Parameters.AddWithValue("$gems", safeGems);
                insertCmd.Parameters.Add("$inventoryBlob", SqliteType.Blob).Value = (object?)initialInventoryBlob ?? DBNull.Value;
                insertCmd.Parameters.AddWithValue("$lastWorld", defaultWorld);
                insertCmd.Parameters.AddWithValue("$createdTicks", nowTicks);
                insertCmd.Parameters.AddWithValue("$lastLoginTicks", nowTicks);

                long newId = (long)(insertCmd.ExecuteScalar() ?? 0L);
                var account = new AccountRecord(
                    AccountId: (int)newId,
                    Username: sanitizedUsername,
                    NormalizedUsername: normalized,
                    Gems: safeGems,
                    InventoryBlob: initialInventoryBlob,
                    LastWorld: defaultWorld,
                    CreatedUtcTicks: nowTicks,
                    LastLoginUtcTicks: nowTicks);

                return new AccountAuthResult(true, account, string.Empty);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (UNIQUE NormalizedUsername)
            {
                return new AccountAuthResult(false, null, "Username sudah digunakan.");
            }
        });
    }

    public AccountAuthResult Authenticate(string username, string password)
    {
        string trimmed = (username ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            string.IsNullOrEmpty(password) ||
            password.Length > NetworkConstants.MaxPasswordLength)
        {
            return new AccountAuthResult(false, null, "Username atau password salah.");
        }

        string normalized = trimmed.ToLowerInvariant();

        return ExecuteWithConnection(connection =>
        {
            AccountRecord? found = null;
            byte[]? storedHash = null;
            byte[]? storedSalt = null;

            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText =
                    """
                    SELECT AccountId, Username, NormalizedUsername, PasswordHash, PasswordSalt,
                           Gems, InventoryBlob, LastWorld, CreatedUtcTicks, LastLoginUtcTicks
                    FROM Accounts
                    WHERE NormalizedUsername = $normalized;
                    """;
                selectCmd.Parameters.AddWithValue("$normalized", normalized);

                using SqliteDataReader reader = selectCmd.ExecuteReader();
                if (!reader.Read())
                {
                    return new AccountAuthResult(false, null, "Username atau password salah.");
                }

                int accountId = reader.GetInt32(0);
                string dbUsername = reader.GetString(1);
                string dbNormalized = reader.GetString(2);
                storedHash = (byte[])reader.GetValue(3);
                storedSalt = (byte[])reader.GetValue(4);
                int gems = reader.GetInt32(5);
                byte[]? invBlob = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6);
                string lastWorld = reader.GetString(7);
                long createdTicks = reader.GetInt64(8);
                long lastLoginTicks = reader.GetInt64(9);

                found = new AccountRecord(
                    accountId,
                    dbUsername,
                    dbNormalized,
                    gems,
                    invBlob,
                    lastWorld,
                    createdTicks,
                    lastLoginTicks);
            }

            if (found is null || storedHash is null || storedSalt is null ||
                !PasswordHasher.VerifyPassword(password, storedHash, storedSalt))
            {
                return new AccountAuthResult(false, null, "Username atau password salah.");
            }

            long loginNowTicks = DateTime.UtcNow.Ticks;
            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.CommandText = "UPDATE Accounts SET LastLoginUtcTicks = $ticks WHERE AccountId = $id;";
                updateCmd.Parameters.AddWithValue("$ticks", loginNowTicks);
                updateCmd.Parameters.AddWithValue("$id", found.AccountId);
                updateCmd.ExecuteNonQuery();
            }

            return new AccountAuthResult(true, found with { LastLoginUtcTicks = loginNowTicks }, string.Empty);
        });
    }

    public AccountRecord? GetById(int accountId)
    {
        if (accountId <= 0)
        {
            return null;
        }

        return ExecuteWithConnection(connection =>
        {
            using var selectCmd = connection.CreateCommand();
            selectCmd.CommandText =
                """
                SELECT AccountId, Username, NormalizedUsername, Gems, InventoryBlob,
                       LastWorld, CreatedUtcTicks, LastLoginUtcTicks
                FROM Accounts
                WHERE AccountId = $id;
                """;
            selectCmd.Parameters.AddWithValue("$id", accountId);

            using SqliteDataReader reader = selectCmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AccountRecord(
                AccountId: reader.GetInt32(0),
                Username: reader.GetString(1),
                NormalizedUsername: reader.GetString(2),
                Gems: reader.GetInt32(3),
                InventoryBlob: reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
                LastWorld: reader.GetString(5),
                CreatedUtcTicks: reader.GetInt64(6),
                LastLoginUtcTicks: reader.GetInt64(7));
        });
    }

    public AccountRecord? GetByUsername(string username)
    {
        string normalized = WorldLockData.NormalizeUsername(username);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return ExecuteWithConnection(connection =>
        {
            using var selectCmd = connection.CreateCommand();
            selectCmd.CommandText =
                """
                SELECT AccountId, Username, NormalizedUsername, Gems, InventoryBlob,
                       LastWorld, CreatedUtcTicks, LastLoginUtcTicks
                FROM Accounts
                WHERE NormalizedUsername = $normalized;
                """;
            selectCmd.Parameters.AddWithValue("$normalized", normalized);

            using SqliteDataReader reader = selectCmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AccountRecord(
                AccountId: reader.GetInt32(0),
                Username: reader.GetString(1),
                NormalizedUsername: reader.GetString(2),
                Gems: reader.GetInt32(3),
                InventoryBlob: reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
                LastWorld: reader.GetString(5),
                CreatedUtcTicks: reader.GetInt64(6),
                LastLoginUtcTicks: reader.GetInt64(7));
        });
    }

    public void SaveProgress(int accountId, int gems, byte[]? inventoryBlob, string lastWorld)
    {
        if (accountId <= 0)
        {
            return;
        }

        int safeGems = Math.Clamp(gems, 0, ItemCatalog.MaxGemBalance);
        string safeWorld = WorldManager.NormalizeWorldName(lastWorld);

        ExecuteWithConnection(connection =>
        {
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText =
                """
                UPDATE Accounts
                SET Gems = $gems,
                    InventoryBlob = $inventoryBlob,
                    LastWorld = $lastWorld
                WHERE AccountId = $id;
                """;
            updateCmd.Parameters.AddWithValue("$gems", safeGems);
            updateCmd.Parameters.Add("$inventoryBlob", SqliteType.Blob).Value = (object?)inventoryBlob ?? DBNull.Value;
            updateCmd.Parameters.AddWithValue("$lastWorld", safeWorld);
            updateCmd.Parameters.AddWithValue("$id", accountId);
            updateCmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Menyimpan <c>InventoryBlob</c> (beserta <c>Gems</c> &amp; <c>LastWorld</c>) milik dua akun trade
    /// di dalam SATU transaksi SQLite atomik (<c>BeginTransaction</c> → <c>Commit</c>).
    /// Jika salah satu akun gagal diperbarui, seluruh transaksi di-rollback untuk mencegah duplikasi atau kehilangan item.
    /// </summary>
    public void SaveTradeProfilesAtomic(AccountProfileSaveEntry accountA, AccountProfileSaveEntry accountB)
    {
        if (accountA.AccountId <= 0 || accountB.AccountId <= 0)
        {
            throw new ArgumentException("Kedua AccountId harus bernilai positif untuk transaksi trade atomik.");
        }

        if (accountA.AccountId == accountB.AccountId)
        {
            throw new ArgumentException("Transaksi trade atomik wajib melibatkan dua AccountId yang berbeda.");
        }

        ExecuteWithConnection(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                int rowsA = ExecuteAccountProfileUpdate(connection, transaction, accountA);
                int rowsB = ExecuteAccountProfileUpdate(connection, transaction, accountB);
                if (rowsA != 1 || rowsB != 1)
                {
                    transaction.Rollback();
                    throw new InvalidOperationException(
                        $"Transaksi trade atomik dibatalkan: akun tidak ditemukan (rowsA={rowsA}, rowsB={rowsB}).");
                }

                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // Transaksi mungkin sudah di-rollback sebelumnya.
                }

                throw;
            }
        });
    }

    public Task SaveTradeProfilesAtomicAsync(
        AccountProfileSaveEntry accountA,
        AccountProfileSaveEntry accountB,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveTradeProfilesAtomic(accountA, accountB);
        return Task.CompletedTask;
    }

    private static int ExecuteAccountProfileUpdate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountProfileSaveEntry entry)
    {
        int safeGems = Math.Clamp(entry.Gems, 0, ItemCatalog.MaxGemBalance);
        string safeWorld = WorldManager.NormalizeWorldName(entry.LastWorld);

        using var updateCmd = connection.CreateCommand();
        updateCmd.Transaction = transaction;
        updateCmd.CommandText =
            """
            UPDATE Accounts
            SET Gems = $gems,
                InventoryBlob = $inventoryBlob,
                LastWorld = $lastWorld
            WHERE AccountId = $id;
            """;
        updateCmd.Parameters.AddWithValue("$gems", safeGems);
        updateCmd.Parameters.Add("$inventoryBlob", SqliteType.Blob).Value = (object?)entry.InventoryBlob ?? DBNull.Value;
        updateCmd.Parameters.AddWithValue("$lastWorld", safeWorld);
        updateCmd.Parameters.AddWithValue("$id", entry.AccountId);
        return updateCmd.ExecuteNonQuery();
    }

    private void ExecuteWithConnection(Action<SqliteConnection> action)
    {
        if (_isInMemory && _inMemoryAnchorConnection is not null)
        {
            lock (_inMemoryLock)
            {
                action(_inMemoryAnchorConnection);
            }

            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        action(connection);
    }

    private T ExecuteWithConnection<T>(Func<SqliteConnection, T> func)
    {
        if (_isInMemory && _inMemoryAnchorConnection is not null)
        {
            lock (_inMemoryLock)
            {
                return func(_inMemoryAnchorConnection);
            }
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return func(connection);
    }

    public void Dispose()
    {
        _inMemoryAnchorConnection?.Dispose();
    }
}
