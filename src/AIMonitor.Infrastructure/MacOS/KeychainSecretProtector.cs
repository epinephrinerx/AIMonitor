using System.Diagnostics;
using System.Security.Cryptography;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>
/// The macOS counterpart of <c>WindowsDpapiProtector</c>: seals the secret store with AES-GCM under
/// a key that lives in the user's login Keychain, so the key is protected by the login password and
/// the sealed file on disk is useless on its own.
///
/// macOS has no DPAPI, and the Keychain stores items rather than transforming blobs, so the key is
/// what goes in the Keychain — one item instead of one per provider, which keeps the access prompt
/// to a single grant and leaves <c>secrets.dat</c> the single source of secrets on both platforms.
/// </summary>
internal sealed class KeychainSecretProtector : ISecretProtector
{
    /// <summary>Scopes the item to this major version, so a future format change can coexist.</summary>
    public const string DefaultService = "AIMonitor2.secrets.v1";
    public const string DefaultAccount = "secret-store-key";

    private const byte FormatVersion = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = 1 + NonceBytes + TagBytes;

    private readonly string _service;
    private readonly string _account;
    private readonly Func<ProcessStartInfo, string?, CommandResult> _run;
    private byte[]? _key;

    public KeychainSecretProtector()
        : this(DefaultService, DefaultAccount, MacOSCommand.Run)
    {
    }

    internal KeychainSecretProtector(
        string service,
        string account,
        Func<ProcessStartInfo, string?, CommandResult> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        _service = service;
        _account = account;
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var key = GetOrCreateKey();
        var sealedBytes = new byte[HeaderBytes + plaintext.Length];
        sealedBytes[0] = FormatVersion;
        var nonce = sealedBytes.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(
            nonce,
            plaintext,
            sealedBytes.AsSpan(1 + NonceBytes + TagBytes),
            sealedBytes.AsSpan(1 + NonceBytes, TagBytes));

        return sealedBytes;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (protectedData.Length < HeaderBytes)
        {
            throw new CryptographicException("The sealed secret store is too short to be valid.");
        }

        if (protectedData[0] != FormatVersion)
        {
            throw new CryptographicException(
                $"The sealed secret store has format version {protectedData[0]}, which this build cannot read.");
        }

        var key = ReadKey()
            ?? throw new CryptographicException(
                "The Keychain holds no key for the secret store, so the stored secrets cannot be read.");

        var plaintext = new byte[protectedData.Length - HeaderBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(
            protectedData.Slice(1, NonceBytes),
            protectedData[HeaderBytes..],
            protectedData.Slice(1 + NonceBytes, TagBytes),
            plaintext);

        return plaintext;
    }

    /// <summary>Removes the key, which makes every sealed secret unreadable. For "clear all".</summary>
    public bool Forget()
    {
        var result = _run(MacOSKeychain.BuildBatchStartInfo(), MacOSKeychain.BuildDeleteCommand(_service, _account) + "\n");
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }

        return result.Succeeded;
    }

    private byte[] GetOrCreateKey()
    {
        var existing = ReadKey();
        if (existing is not null)
        {
            return existing;
        }

        var created = RandomNumberGenerator.GetBytes(KeyBytes);
        // The command text carries the key base64-encoded, so a managed string holds a copy until
        // the GC reclaims it. That is unavoidable with batch mode and still far better than argv,
        // which any other process of this user could read while the command runs.
        var result = _run(
            MacOSKeychain.BuildBatchStartInfo(),
            MacOSKeychain.BuildAddCommand(_service, _account, Convert.ToBase64String(created)) + "\n");
        if (!result.Succeeded)
        {
            CryptographicOperations.ZeroMemory(created);
            throw new CryptographicException(
                $"The Keychain refused to store the secret-store key (security exit {result.ExitCode}).");
        }

        _key = created;
        return created;
    }

    private byte[]? ReadKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        var result = _run(MacOSKeychain.BuildReadStartInfo(_service, _account), null);
        if (!result.Succeeded)
        {
            // Exit 44 is "item not found", which is the ordinary first-run case. Anything else is a
            // real failure, but it is reported the same way: the caller must not overwrite a key it
            // merely failed to read, so creating one is the caller's decision, not this method's.
            return null;
        }

        // security prints a trailing newline of its own; base64 never ends in whitespace.
        var encoded = result.StandardOutput.TrimEnd('\r', '\n');
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException(
                "The Keychain item for the secret store is not valid base64, so it was not written by this app.",
                exception);
        }

        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException(
                $"The Keychain item for the secret store is {key.Length} bytes, not {KeyBytes}.");
        }

        _key = key;
        return key;
    }
}
