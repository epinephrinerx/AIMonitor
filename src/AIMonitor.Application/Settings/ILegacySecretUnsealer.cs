namespace AIMonitor.Application.Settings;

public interface ILegacySecretUnsealer
{
    bool TryUnseal(string protectedValue, out string plaintext);
}
