namespace AIMonitor.Application.Settings;

/// <summary>Read-only boundary over the legacy Python application's Registry tree.</summary>
public interface ILegacySettingsReader
{
    IReadOnlyDictionary<string, object?> ReadAll();
}
