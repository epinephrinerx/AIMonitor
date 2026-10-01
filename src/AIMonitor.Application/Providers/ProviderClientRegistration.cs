namespace AIMonitor.Application.Providers;

public sealed record ProviderClientRegistration
{
    public string ProviderId { get; }
    public IProviderQuotaClient Client { get; }

    public ProviderClientRegistration(string providerId, IProviderQuotaClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(client);
        ProviderId = providerId.Trim();
        Client = client;
    }
}
