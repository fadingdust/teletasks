using Xunit;

namespace TeleTasks.Tests;

/// <summary>
/// Tests in this collection mutate process-wide state (typically the
/// <c>TELETASKS_CONFIG_DIR</c> env var) and must not run concurrently with
/// each other or with anything else that reads the same state.
/// </summary>
[CollectionDefinition("EnvironmentMutating", DisableParallelization = true)]
public sealed class EnvironmentMutatingCollection
{
}
