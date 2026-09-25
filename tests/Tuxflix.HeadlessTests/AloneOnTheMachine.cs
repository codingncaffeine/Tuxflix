using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Tests that time the machine's own work run after the others, one at a time: a band measured
/// while the rest of the suite runs measures the suite.
/// </summary>
[CollectionDefinition(nameof(AloneOnTheMachine), DisableParallelization = true)]
public sealed class AloneOnTheMachine;
