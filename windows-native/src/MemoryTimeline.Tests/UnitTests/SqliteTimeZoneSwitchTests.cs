using FluentAssertions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The test assembly must run with the same Microsoft.Data.Sqlite date
/// semantics the app sets first thing in <c>Program.Main</c>; see
/// <see cref="TestAssemblyInitializer"/>. A suite without the switch would be
/// green or red on semantics the app never ships with.
/// </summary>
public class SqliteTimeZoneSwitchTests
{
    [Fact]
    public void Pre10TimeZoneHandling_IsOnForTheWholeTestRun()
    {
        AppContext.TryGetSwitch(TestAssemblyInitializer.Pre10TimeZoneHandlingSwitch, out var enabled).Should().BeTrue();
        enabled.Should().BeTrue("Program.Main sets this switch before anything touches SQLite, and so must the tests");
    }
}
