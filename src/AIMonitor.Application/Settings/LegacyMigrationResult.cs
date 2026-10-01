namespace AIMonitor.Application.Settings;

public sealed record LegacyMigrationResult(
    bool Migrated,
    int ImportedSettings,
    int ImportedSecrets,
    IReadOnlyList<string> Warnings);
