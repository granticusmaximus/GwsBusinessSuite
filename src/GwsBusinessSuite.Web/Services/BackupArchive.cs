using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.Web.Services;

public sealed record BackupManifest(string Format, DateTimeOffset CreatedAtUtc, IReadOnlyList<BackupManifestFile> Files);
public sealed record BackupManifestFile(string Path, long Size, string Sha256);

internal static class BackupArchive
{
    private static readonly byte[] Magic = "GWSBKP01"u8.ToArray();
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static byte[] ParseKey(string value)
    {
        try
        {
            var key = Convert.FromBase64String(value.Trim());
            if (key.Length != 32) throw new FormatException();
            return key;
        }
        catch (FormatException) { throw new InvalidOperationException("The backup encryption key must be a Base64-encoded 32-byte value."); }
    }

    public static bool HasHeader(string path)
    {
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(path);
        var header = new byte[Magic.Length];
        return input.Read(header) == header.Length && header.SequenceEqual(Magic);
    }

    public static async Task<BackupManifest> CreateManifestAsync(string root, string timestamp, CancellationToken cancellationToken)
    {
        var files = new List<BackupManifestFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            await using var stream = File.OpenRead(file);
            files.Add(new(relative, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))));
        }
        return new("GWS encrypted backup v1", DateTimeOffset.ParseExact(timestamp, "yyyyMMdd'T'HHmmssfff'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal), files);
    }

    public static async Task VerifyManifestAsync(string root, BackupManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Format != "GWS encrypted backup v1") throw new InvalidDataException("Unsupported backup format.");
        var expectedPaths = manifest.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var actualPaths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(path => path != "manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(expectedPaths)) throw new InvalidDataException("The backup contents do not match its manifest.");
        foreach (var item in manifest.Files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, item.Path));
            if (!fullPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("The backup manifest contains an unsafe path.");
            if (!File.Exists(fullPath)) throw new InvalidDataException($"Backup file is missing: {item.Path}");
            await using var stream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (stream.Length != item.Size || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(item.Sha256)))
                throw new InvalidDataException($"Backup file integrity failed: {item.Path}");
        }
    }

    public static async Task EncryptAsync(string sourcePath, string destinationPath, byte[] masterKey, CancellationToken cancellationToken)
    {
        var keys = SHA512.HashData(masterKey); var encryptionKey = keys[..32]; var macKey = keys[32..];
        var iv = RandomNumberGenerator.GetBytes(16);
        await using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            await output.WriteAsync(Magic, cancellationToken); await output.WriteAsync(iv, cancellationToken);
            using var aes = Aes.Create(); aes.Key = encryptionKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            await using var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, true);
            await using var input = File.OpenRead(sourcePath); await input.CopyToAsync(crypto, cancellationToken); await crypto.FlushFinalBlockAsync(cancellationToken);
        }
        byte[] tag;
        using (var hmac = new HMACSHA256(macKey)) await using (var input = File.OpenRead(destinationPath)) tag = await hmac.ComputeHashAsync(input, cancellationToken);
        await using var append = new FileStream(destinationPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, true);
        await append.WriteAsync(tag, cancellationToken);
        CryptographicOperations.ZeroMemory(keys);
    }

    public static async Task DecryptAsync(string sourcePath, string destinationPath, byte[] masterKey, CancellationToken cancellationToken)
    {
        var keys = SHA512.HashData(masterKey); var encryptionKey = keys[..32]; var macKey = keys[32..];
        var info = new FileInfo(sourcePath); if (info.Length < Magic.Length + 16 + 32) throw new InvalidDataException("Backup archive is truncated.");
        var ciphertextPath = Path.Combine(Path.GetTempPath(), $"gws-cipher-{Guid.NewGuid():N}");
        try
        {
            byte[] iv = new byte[16], storedTag = new byte[32];
            await using (var input = File.OpenRead(sourcePath))
            {
                var magic = new byte[Magic.Length]; await input.ReadExactlyAsync(magic, cancellationToken);
                if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Backup archive header is invalid.");
                await input.ReadExactlyAsync(iv, cancellationToken);
                var cipherLength = info.Length - Magic.Length - iv.Length - storedTag.Length;
                await using var cipher = File.Create(ciphertextPath);
                await CopyExactlyAsync(input, cipher, cipherLength, cancellationToken);
                await input.ReadExactlyAsync(storedTag, cancellationToken);
            }
            byte[] computedTag;
            using (var hmac = new HMACSHA256(macKey))
            {
                await using var authenticated = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                computedTag = await ComputePrefixHashAsync(hmac, authenticated, info.Length - storedTag.Length, cancellationToken);
            }
            if (!CryptographicOperations.FixedTimeEquals(storedTag, computedTag)) throw new CryptographicException("Backup authentication failed.");
            using var aes = Aes.Create(); aes.Key = encryptionKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            await using var cipherInput = File.OpenRead(ciphertextPath); await using var crypto = new CryptoStream(cipherInput, aes.CreateDecryptor(), CryptoStreamMode.Read);
            await using var output = File.Create(destinationPath); await crypto.CopyToAsync(output, cancellationToken);
        }
        finally { if (File.Exists(ciphertextPath)) File.Delete(ciphertextPath); CryptographicOperations.ZeroMemory(keys); }
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long bytes, CancellationToken token)
    {
        var buffer = new byte[81920];
        while (bytes > 0) { var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), token); if (read == 0) throw new EndOfStreamException(); await output.WriteAsync(buffer.AsMemory(0, read), token); bytes -= read; }
    }

    private static async Task<byte[]> ComputePrefixHashAsync(HMAC hmac, Stream input, long bytes, CancellationToken token)
    {
        var buffer = new byte[81920];
        while (bytes > 0) { var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), token); if (read == 0) throw new EndOfStreamException(); hmac.TransformBlock(buffer, 0, read, null, 0); bytes -= read; }
        hmac.TransformFinalBlock([], 0, 0); return hmac.Hash!;
    }
}
