using Xunit;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// Marker for the single collection that every UI smoke test class joins via
/// <c>[Collection("UI smoke")]</c>. xunit serialises tests within a
/// collection, so this guarantees no two FlaUI-driven tests can ever overlap
/// — even if a future config tweak re-enables parallelism elsewhere.
/// </summary>
[CollectionDefinition("UI smoke", DisableParallelization = true)]
public sealed class UiSmokeCollection { }
