using System.Runtime.Versioning;
using System.Security.Cryptography;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.Linux;

internal enum LinuxKeySource : byte
{
    SecretService = 1,
    KeyFile = 2
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxSecretProtector : ISecretProtector
{
    public const string KeyFileName = "secrets.key";
    public const string LockFileName = "secrets.key.lock";
    public const string SchemaName = "net.apichart.aiusagemonitor.SecretStoreKey";
    public static readonly TimeSpan DefaultKeyLockTimeout = TimeSpan.FromSeconds(65);

    private const int HeaderBytes = 30;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private readonly ISecretServiceKeyring _keyring;
    private readonly TimeSpan? _keyLockTimeout;
    private LinuxKeySource? _lastSource;

    internal LinuxSecretProtector(
        string service,
        string account,
        ISecretServiceKeyring keyring,
        string keyFilePath,
        TimeSpan? keyLockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(keyring);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFilePath);
        if (!Path.IsPathFullyQualified(keyFilePath))
            throw new ArgumentException("The key file path must be fully qualified.", nameof(keyFilePath));
        if (keyLockTimeout is { } timeout && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keyLockTimeout));

        Service = service;
        Account = account;
        _keyring = keyring;
        KeyFilePath = keyFilePath;
        _keyLockTimeout = keyLockTimeout;
        LockFilePath = Path.Combine(Path.GetDirectoryName(keyFilePath)!, LockFileName);
    }

    public string Service { get; }
    public string Account { get; }
    public string KeyFilePath { get; }
    public string LockFilePath { get; }

    internal static IReadOnlyDictionary<string, string> KeyAttributes(string service, string account) =>
        new Dictionary<string, string> { ["service"] = service, ["account"] = account };

    internal static string KeyLabel(string service) => $"AI Usage Monitor secret store key ({service})";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var key = GetProtectKey(out var source);
        try
        {
            var sealedData = new byte[HeaderBytes + plaintext.Length];
            sealedData[0] = 0x01;
            sealedData[1] = (byte)source;
            RandomNumberGenerator.Fill(sealedData.AsSpan(2, NonceBytes));
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(
                sealedData.AsSpan(2, NonceBytes),
                plaintext,
                sealedData.AsSpan(HeaderBytes),
                sealedData.AsSpan(14, TagBytes),
                sealedData.AsSpan(0, 2));
            _lastSource = source;
            return sealedData;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (protectedData.Length < HeaderBytes)
            throw new CryptographicException("The sealed secret store is too short to be valid.");
        if (protectedData[0] != 0x01)
            throw new CryptographicException(
                $"The sealed secret store has format version {protectedData[0]}, which this build cannot read.");
        if (protectedData[1] is not (1 or 2))
            throw new CryptographicException(
                $"The sealed secret store names an unknown key source {protectedData[1]}.");

        var source = (LinuxKeySource)protectedData[1];
        var key = source == LinuxKeySource.SecretService
            ? LookupSecretServiceKey()
            : SecretKeyFile.TryRead(KeyFilePath)
                ?? throw new CryptographicException("The secret store was sealed with a key file that no longer exists.");
        try
        {
            var plaintext = new byte[protectedData.Length - HeaderBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(
                protectedData.Slice(2, NonceBytes),
                protectedData[HeaderBytes..],
                protectedData.Slice(14, TagBytes),
                plaintext,
                protectedData[..2]);
            _lastSource = source;
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] GetProtectKey(out LinuxKeySource source)
    {
        if (_lastSource == LinuxKeySource.KeyFile)
        {
            source = LinuxKeySource.KeyFile;
            return SecretKeyFile.ReadOrCreate(KeyFilePath);
        }

        var directory = Path.GetDirectoryName(KeyFilePath)!;
        if (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null)
            throw new CryptographicException("The secret store folder is a symbolic link; refusing to use it.");
        try
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException exception)
        {
            throw new CryptographicException("Cannot prepare the secret store folder.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new CryptographicException("Cannot prepare the secret store folder.", exception);
        }

        FileStream? keyLock;
        try
        {
            keyLock = LinuxFileLock.TryAcquire(LockFilePath, _keyLockTimeout ?? DefaultKeyLockTimeout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException("Cannot lock the secret store key.", exception);
        }
        if (keyLock is null)
            throw new CryptographicException("Another copy of AI Usage Monitor is creating the secret store key; try again.");

        using (keyLock)
        {
            var lookup = _keyring.Lookup(SchemaName, KeyAttributes(Service, Account));
            if (lookup.Status == SecretServiceStatus.Found)
            {
                source = LinuxKeySource.SecretService;
                return ParseCanonicalKey(lookup.Secret);
            }
            if (lookup.Status == SecretServiceStatus.Failed)
                throw KeyringError("The Secret Service could not be asked for the secret store key.", lookup);
            if (lookup.Status == SecretServiceStatus.Unavailable)
            {
                if (_lastSource is not null)
                    throw KeyringError("The secret store uses the Secret Service, which is not available in this session; nothing was saved.", lookup);
                source = LinuxKeySource.KeyFile;
                return SecretKeyFile.ReadOrCreate(KeyFilePath);
            }

            var created = RandomNumberGenerator.GetBytes(KeyBytes);
            var store = _keyring.Store(
                SchemaName,
                KeyAttributes(Service, Account),
                KeyLabel(Service),
                Convert.ToBase64String(created));
            if (store.Status == SecretServiceStatus.Done)
            {
                source = LinuxKeySource.SecretService;
                return created;
            }
            CryptographicOperations.ZeroMemory(created);
            if (store.Status == SecretServiceStatus.Unavailable && _lastSource is null)
            {
                source = LinuxKeySource.KeyFile;
                return SecretKeyFile.ReadOrCreate(KeyFilePath);
            }
            if (store.Status == SecretServiceStatus.Unavailable)
                throw KeyringError("The secret store uses the Secret Service, which is not available in this session; nothing was saved.", store);
            throw KeyringError("The Secret Service refused to store the secret store key.", store);
        }
    }

    private byte[] LookupSecretServiceKey()
    {
        var result = _keyring.Lookup(SchemaName, KeyAttributes(Service, Account));
        return result.Status switch
        {
            SecretServiceStatus.Found => ParseCanonicalKey(result.Secret),
            SecretServiceStatus.NotFound => throw KeyringError(
                "The secret store was sealed with a key from the Secret Service, but the Secret Service holds no such key.", result),
            SecretServiceStatus.Unavailable => throw KeyringError(
                "The secret store was sealed with a key from the Secret Service, which is not available in this session; " +
                "the stored secrets were left unchanged.", result),
            _ => throw KeyringError(
                "The Secret Service could not provide the secret store key; the stored secrets were left unchanged.", result)
        };
    }

    private static byte[] ParseCanonicalKey(string? secret)
    {
        if (secret is not null)
        {
            var key = new byte[KeyBytes];
            if (Convert.TryFromBase64String(secret, key, out var written) && written == KeyBytes &&
                string.Equals(Convert.ToBase64String(key), secret, StringComparison.Ordinal))
                return key;
            CryptographicOperations.ZeroMemory(key);
        }
        throw new CryptographicException("The Secret Service item for the secret store key was not written by this app.");
    }

    private static CryptographicException KeyringError(string message, SecretServiceResult result) =>
        new($"{message} ({result.Status}: {SecretServiceErrors.SafeDetail(result.Message)})");
}
