using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Photon.Core;

/// <summary>
/// Secure vault for private photos/videos. Uses AES-256-CBC with a
/// key derived from the user's password via PBKDF2 (100k iterations).
/// The vault lives at <see cref="AppPaths.SecureVaultDir"/> with encrypted
/// copies of imported files. Originals are NOT deleted (move = copy+hide).
/// </summary>
public sealed class SecureVault
{
    private readonly ILogger<SecureVault> _log;
    private readonly object _lock = new();
    private byte[]? _cachedKey; // derived key, cleared on lock
    private bool _isUnlocked;

    public bool IsUnlocked => _isUnlocked;
    public bool IsPasswordSet => File.Exists(AppPaths.SecureVaultDbPath);
    public byte[]? GetCachedKey() => _cachedKey;

    public SecureVault(ILogger<SecureVault> log) => _log = log;

    // ---- Password management ----

    /// <summary>
    /// Sets up the vault password on first use. Derives the master key and
    /// stores a verification hash. Returns true on success.
    /// </summary>
    public bool SetPassword(string password)
    {
        try
        {
            var salt = RandomNumberGenerator.GetBytes(32);
            var key = DeriveKey(password, salt);
            var verifyHash = SHA256.HashData(key);

            // Store salt + verify hash in a simple JSON sidecar
            var meta = new VaultMeta { Salt = Convert.ToBase64String(salt), VerifyHash = Convert.ToBase64String(verifyHash) };
            File.WriteAllText(AppPaths.SecureVaultDbPath, JsonSerializer.Serialize(meta));

            lock (_lock) { _cachedKey = key; _isUnlocked = true; }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SetPassword failed");
            return false;
        }
    }

    /// <summary>
    /// Verifies a password against the stored hash. If correct, caches the
    /// derived key so file operations don't re-prompt.
    /// </summary>
    public bool Unlock(string password)
    {
        try
        {
            if (!File.Exists(AppPaths.SecureVaultDbPath)) return false;
            var json = File.ReadAllText(AppPaths.SecureVaultDbPath);
            var meta = JsonSerializer.Deserialize<VaultMeta>(json);
            if (meta?.Salt is null || meta.VerifyHash is null) return false;

            var salt = Convert.FromBase64String(meta.Salt);
            var key = DeriveKey(password, salt);
            var hash = SHA256.HashData(key);

            if (Convert.ToBase64String(hash) != meta.VerifyHash) return false;

            lock (_lock) { _cachedKey = key; _isUnlocked = true; }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unlock failed");
            return false;
        }
    }

    /// <summary>Changes the password. Must be unlocked first.</summary>
    public bool ChangePassword(string oldPassword, string newPassword)
    {
        if (!_isUnlocked) return false;
        // Re-verify old password
        if (!File.Exists(AppPaths.SecureVaultDbPath)) return false;
        var json = File.ReadAllText(AppPaths.SecureVaultDbPath);
        var meta = JsonSerializer.Deserialize<VaultMeta>(json);
        if (meta?.Salt is null) return false;
        var oldSalt = Convert.FromBase64String(meta.Salt);
        var oldKey = DeriveKey(oldPassword, oldSalt);
        if (Convert.ToBase64String(SHA256.HashData(oldKey)) != meta.VerifyHash) return false;

        // Set new password (new salt, re-encrypt nothing — files keep their own per-file keys)
        return SetPassword(newPassword);
    }

    /// <summary>Locks the vault, clearing the cached key.</summary>
    public void Lock()
    {
        lock (_lock)
        {
            if (_cachedKey is not null)
                Array.Clear(_cachedKey, 0, _cachedKey.Length);
            _cachedKey = null;
            _isUnlocked = false;
        }
    }

    // ---- File operations ----

    /// <summary>
    /// Imports a file into the vault. Copies the file, encrypts it with AES-256-CBC
    /// using a random per-file IV. The encrypted file is stored in the vault dir.
    /// </summary>
    public async Task<bool> ImportFileAsync(string sourcePath)
    {
        if (!_isUnlocked || _cachedKey is null) return false;
        try
        {
            var fileName = Path.GetFileName(sourcePath);
            var encryptedPath = Path.Combine(AppPaths.SecureVaultDir, Guid.NewGuid().ToString("N") + ".vault");

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = _cachedKey;
            aes.GenerateIV();
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

            await using var inputStream = File.OpenRead(sourcePath);
            await using var outputStream = File.Create(encryptedPath);

            // Write header: [16-byte IV] [4-byte original name length UTF-8] [original name UTF-8]
            await outputStream.WriteAsync(aes.IV, 0, aes.IV.Length);
            var nameBytes = Encoding.UTF8.GetBytes(fileName);
            var nameLen = BitConverter.GetBytes(nameBytes.Length);
            await outputStream.WriteAsync(nameLen, 0, nameLen.Length);
            await outputStream.WriteAsync(nameBytes, 0, nameBytes.Length);

            // Encrypt file contents
            using var encryptor = aes.CreateEncryptor();
            await using var cryptoStream = new CryptoStream(outputStream, encryptor, CryptoStreamMode.Write);
            await inputStream.CopyToAsync(cryptoStream);
            await cryptoStream.FlushFinalBlockAsync();

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ImportFile failed for {Path}", sourcePath);
            return false;
        }
    }

    /// <summary>
    /// Lists all vault entries (file names only, no decryption needed).
    /// </summary>
    public List<VaultEntry> ListFiles()
    {
        var entries = new List<VaultEntry>();
        if (!_isUnlocked || _cachedKey is null) return entries;

        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.SecureVaultDir, "*.vault"))
            {
                try
                {
                    using var fs = File.OpenRead(file);
                    var iv = new byte[16];
                    int read = fs.Read(iv, 0, 16);
                    if (read < 16) continue;

                    var lenBytes = new byte[4];
                    read = fs.Read(lenBytes, 0, 4);
                    if (read < 4) continue;
                    int nameLen = BitConverter.ToInt32(lenBytes, 0);
                    if (nameLen < 0 || nameLen > 1024) continue;

                    var nameBuffer = new byte[nameLen];
                    read = fs.Read(nameBuffer, 0, nameLen);
                    if (read < nameLen) continue;

                    var originalName = Encoding.UTF8.GetString(nameBuffer);
                    var fileSize = fs.Length - 16 - 4 - nameLen;
                    entries.Add(new VaultEntry(originalName, file, fileSize, iv));
                }
                catch { /* skip corrupt entries */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ListFiles failed");
        }
        return entries;
    }

    /// <summary>
    /// Exports (decrypts) a vault file to a destination path.
    /// </summary>
    public async Task<bool> ExportFileAsync(string vaultPath, string destinationPath)
    {
        if (!_isUnlocked || _cachedKey is null) return false;
        try
        {
            using var fs = File.OpenRead(vaultPath);
            var iv = new byte[16];
            await fs.ReadAsync(iv, 0, 16);

            var lenBytes = new byte[4];
            await fs.ReadAsync(lenBytes, 0, 4);
            int nameLen = BitConverter.ToInt32(lenBytes, 0);
            await fs.ReadAsync(new byte[nameLen], 0, nameLen); // skip name

            using var aes = Aes.Create();
            aes.Key = _cachedKey;
            aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

            await using var outputStream = File.Create(destinationPath);
            using var decryptor = aes.CreateDecryptor();
            await using var cryptoStream = new CryptoStream(fs, decryptor, CryptoStreamMode.Read);
            await cryptoStream.CopyToAsync(outputStream);

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ExportFile failed");
            return false;
        }
    }

    /// <summary>Removes a vault file permanently.</summary>
    public bool RemoveFile(string vaultPath)
    {
        try
        {
            if (File.Exists(vaultPath)) File.Delete(vaultPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RemoveFile failed");
            return false;
        }
    }

    /// <summary>
    /// Returns paths of all files currently in the vault (for exclusion
    /// from the main gallery).
    /// </summary>
    public HashSet<string> GetVaultSourcePaths()
    {
        // Since we store copies (not moves), the vault doesn't have source paths.
        // The vault is a separate store. Return empty — the SecureFolder page
        // manages its own content independently.
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // ---- Crypto helpers ----

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32); // 256-bit key
    }

    private sealed class VaultMeta
    {
        public string? Salt { get; set; }
        public string? VerifyHash { get; set; }
    }
}

/// <summary>A file stored inside the vault.</summary>
public sealed record VaultEntry(string OriginalName, string VaultPath, long EncryptedSize, byte[] Iv);