using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Tests that show a window of their own and give it the keyboard run after the others, one at a
/// time: the headless platform has one keyboard, and a window a test shows takes it from the
/// windows of the tests running beside it.
/// </summary>
[CollectionDefinition(nameof(KeyboardFocus), DisableParallelization = true)]
public sealed class KeyboardFocus;
