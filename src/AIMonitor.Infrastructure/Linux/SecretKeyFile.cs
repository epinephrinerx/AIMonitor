using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AIMonitor.Infrastructure.Linux;

[SupportedOSPlatform("linux")]
internal static class SecretKeyFile
{
    public const int KeyBytes = 32;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode KeyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static byte[]? TryRead(string path)
    {
        Validate(path);
        var dir = Path.GetDirectoryName(path)!;
        try
        {
            if (new FileInfo(path).LinkTarget is not null) throw new CryptographicException($"The key file '{path}' must not be a symbolic link.");
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is not null)
                throw new CryptographicException($"The key directory '{dir}' must not be a symbolic link.");
            if (Directory.Exists(path)) throw new CryptographicException($"The key file path '{path}' is a directory.");
            if (!File.Exists(path)) return null;
            if ((File.GetUnixFileMode(dir) & (UnixFileMode)0x0fff) != DirectoryMode) File.SetUnixFileMode(dir, DirectoryMode);
            if ((File.GetUnixFileMode(path) & (UnixFileMode)0x0fff) != KeyFileMode) File.SetUnixFileMode(path, KeyFileMode);
            var key = File.ReadAllBytes(path);
            if (key.Length != KeyBytes)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException($"The key file '{path}' must contain exactly {KeyBytes} bytes.");
            }
            return key;
        }
        catch (CryptographicException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new CryptographicException($"Could not read the key file '{path}'.", ex); }
    }

    public static byte[] ReadOrCreate(string path)
    {
        Validate(path);
        var existing = TryRead(path);
        if (existing is not null) return existing;
        var dir = Path.GetDirectoryName(path)!;
        string? temp = null;
        byte[]? key = null;
        try
        {
            Directory.CreateDirectory(dir, DirectoryMode);
            File.SetUnixFileMode(dir, DirectoryMode);
            key = RandomNumberGenerator.GetBytes(KeyBytes);
            temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = KeyFileMode
            }))
            {
                stream.Write(key);
                stream.Flush(true);
            }
            File.SetUnixFileMode(temp, KeyFileMode);
            // Publish the complete key without replacing a file created by a concurrent caller (R-024).
            var linkResult = Link(temp, path);
            if (linkResult == 0)
            {
                BestEffortDelete(temp);
                temp = null;
                var result = key;
                key = null;
                return result;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == 17)
            {
                BestEffortDelete(temp);
                temp = null;
                CryptographicOperations.ZeroMemory(key); key = null;
                return TryRead(path) ?? throw new CryptographicException($"The key file '{path}' changed while it was being created.");
            }
            throw new CryptographicException($"Could not publish the key file '{path}': link failed (errno {errno}).");
        }
        catch (CryptographicException) { throw; }
        catch (DllNotFoundException ex)
        { throw new CryptographicException($"Could not publish the key file '{path}'.", ex); }
        catch (EntryPointNotFoundException ex)
        { throw new CryptographicException($"Could not publish the key file '{path}'.", ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new CryptographicException($"Could not create the key file '{path}'.", ex); }
        finally
        {
            if (temp is not null) BestEffortDelete(temp);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The key file path must be fully qualified.", nameof(path));
    }
    private static void BestEffortDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [DllImport("libc.so.6", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
