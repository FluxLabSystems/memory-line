using System.Runtime.CompilerServices;

namespace MemoryTimeline.Portability.Tests;

/// <summary>
/// Runs before any test in this assembly. Sets the same runtime switch the
/// Windows app sets first thing in <c>Program.Main</c>
/// (<c>Microsoft.Data.Sqlite.Pre10TimeZoneHandling</c>): the archive stack must
/// behave off Windows the way it behaves on Windows, and the SQLite date
/// semantics are part of that behaviour. A second desktop head will need to
/// set the same switch at startup.
/// </summary>
internal static class PortabilityTestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppContext.SetSwitch("Microsoft.Data.Sqlite.Pre10TimeZoneHandling", true);
    }
}
