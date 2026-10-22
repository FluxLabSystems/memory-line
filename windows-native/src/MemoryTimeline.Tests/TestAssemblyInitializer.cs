using System.Runtime.CompilerServices;

namespace MemoryTimeline.Tests;

/// <summary>
/// Runs before any test in this assembly. Sets the same runtime switch the
/// app sets first thing in <c>Program.Main</c>, so the tests exercise the
/// SQLite date semantics the app actually ships with.
///
/// <para>.NET 10 changed Microsoft.Data.Sqlite's timezone handling: a stored
/// timestamp without an offset now reads as UTC rather than local, and one
/// with an offset comes back converted to UTC. Every date in an existing
/// memory-timeline.db was written under the old rules, so the app keeps the
/// pre-10 behaviour until the stored values are audited. A test suite running
/// without the switch would pass or fail on semantics the app never uses —
/// exactly the kind of green that hides a real bug.</para>
/// </summary>
internal static class TestAssemblyInitializer
{
    /// <summary>The switch name, shared with <c>Program.Main</c>.</summary>
    internal const string Pre10TimeZoneHandlingSwitch = "Microsoft.Data.Sqlite.Pre10TimeZoneHandling";

    [ModuleInitializer]
    internal static void Initialize()
    {
        AppContext.SetSwitch(Pre10TimeZoneHandlingSwitch, true);
    }
}
