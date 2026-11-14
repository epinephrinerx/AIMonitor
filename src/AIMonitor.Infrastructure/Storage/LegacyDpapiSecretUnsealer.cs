using System.Text;
using AIMonitor.Application.Settings;

namespace AIMonitor.Infrastructure.Storage;

public sealed class LegacyDpapiSecretUnsealer : ILegacySecretUnsealer
{
    private static readonly string[] Entropies = [
        "AIUsageMonitor.providerKeys.v1",
        "ClaudeUsageMonitor.providerKeys.v1",
    ];

    public bool TryUnseal(string protectedValue, out string plaintext)
    {
        plaintext = string.Empty;
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            foreach (var entropy in Entropies)
            {
                try
                {
                    var bytes = new WindowsDpapiProtector(entropy).Unprotect(encrypted);
                    try
                    {
                        plaintext = new UTF8Encoding(false, true).GetString(bytes);
                        return true;
                    }
                    finally
                    {
                        Array.Clear(bytes);
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DecoderFallbackException)
                {
                }
            }

            return false;
        }
        finally
        {
            Array.Clear(encrypted);
        }
    }
}
