namespace AIMonitor.Infrastructure.Tests;

/// <summary>
/// Tests that set a process environment variable, or call code that reads one, join this collection.
/// It turns parallelism off for its members, so no other test runs while the environment is changed.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
