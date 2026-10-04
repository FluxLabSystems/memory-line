# Memory Timeline - Testing Guide

This document describes the testing strategy and how to run tests for the Memory Timeline Windows native application.

> **How to run these tests (read first).** The test project references the WinUI 3 app, and that app cannot be built with the `dotnet` CLI (its WinUI PRI/XAML resource tooling is a .NET Framework MSBuild task that fails under the `dotnet` build engine). So **`dotnet test` does not work** — it would rebuild the WinUI app and fail. Instead you **build the solution once with Visual Studio `msbuild.exe`** and then run the already-built test assembly with **`dotnet vstest`**. See [Running Tests](#running-tests). This is also what CI does.

## Test Structure

```
MemoryTimeline.Tests/
├── TestDbContextFactory.cs     # Test IDbContextFactory<AppDbContext> (shared by most tests)
├── Services/                   # Service-level unit tests (in-memory DB)
│   ├── EventServiceTests.cs
│   ├── TimelineServiceTests.cs
│   └── SettingsServiceTests.cs
├── UnitTests/                  # Additional unit tests
│   ├── QueueServiceTests.cs    # Pure mock-based (no DbContext)
│   ├── ExportServiceTests.cs
│   ├── ImportServiceTests.cs
│   └── RagServiceTests.cs
├── Models/                     # Pure model/logic tests (no DB)
│   ├── EventLayoutEngineTests.cs
│   ├── TimelineViewportTests.cs
│   └── TimelineScaleTests.cs
├── Integration/                # Integration tests against a real SQLite file DB
│   └── DatabaseIntegrationTests.cs
└── Performance/                # Performance / load tests (SQLite file DB)
    └── PerformanceTests.cs
```

## Test Framework and Tools

- **xUnit**: Test framework
- **Moq**: Mocking framework for dependencies
- **FluentAssertions**: Fluent assertion library for readable assertions
- **EF Core InMemory**: In-memory database for service/unit tests
- **SQLite (file-based)**: Real database for integration and performance tests
- **Coverlet**: Code coverage tool

### DbContext factory pattern (important for test authors)

Data access in the app now goes through **`IDbContextFactory<AppDbContext>`**: services and repositories open a short-lived `AppDbContext` per operation instead of holding a long-lived shared context. The tests mirror this via `TestDbContextFactory`, which wraps a single `DbContextOptions` so every `CreateDbContext()` returns a **new** context over the **same** database. Two provider caveats it encodes:

- **EF Core InMemory** — all contexts built from the same options instance share the same in-memory store, so a factory over a uniquely named InMemory DB works. Use `TestDbContextFactory.CreateInMemory()`.
- **SQLite `:memory:`** — does **not** work with the factory, because every new connection gets a fresh, empty database. Tests that need SQLite must use a **file-based temp database** (`TestDbContextFactory.CreateSqliteFile(path)`), which is why the integration and performance tests create a temp `.db` file rather than an in-memory SQLite connection.

Integration/performance tests build their schema with `Database.EnsureCreated()` (there are no EF migrations in the repo — the app uses a hand-rolled `SchemaUpgrader` at runtime instead).

## Running Tests

> **Do not use `dotnet test`** (it rebuilds the WinUI app with the `dotnet` engine and fails). Build once with VS `msbuild.exe`, then run the built assembly with `dotnet vstest`.

### Build once, then run
```powershell
cd windows-native/src

# 1. Build the solution (Release | x64) with Visual Studio MSBuild.
#    'msbuild' here is the VS msbuild.exe (e.g. Developer PowerShell for VS 2026),
#    NOT 'dotnet build'.
msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /m

# 2. Run the already-built test assembly (no rebuild).
$dll = "MemoryTimeline.Tests/bin/x64/Release/net8.0-windows10.0.26100.0/MemoryTimeline.Tests.dll"
dotnet vstest $dll --logger:"trx;LogFileName=test.trx" --ResultsDirectory:TestResults
```

### By Category / Class / Method
`dotnet vstest` filters with `--TestCaseFilter`:
```powershell
dotnet vstest $dll --TestCaseFilter:"FullyQualifiedName~UnitTests"
dotnet vstest $dll --TestCaseFilter:"FullyQualifiedName~Integration"
dotnet vstest $dll --TestCaseFilter:"FullyQualifiedName~Performance"
dotnet vstest $dll --TestCaseFilter:"FullyQualifiedName~QueueServiceTests"
dotnet vstest $dll --TestCaseFilter:"FullyQualifiedName=MemoryTimeline.Tests.UnitTests.QueueServiceTests.AddToQueueAsync_ValidRecording_AddsToQueue"
```

> **Environment note.** Running a self-contained WinUI 3 test assembly under headless VSTest is finicky; in CI this step is treated as best-effort (it does not gate the build). If the app assembly fails to build for any reason, the test project — which references it — will also fail to build, so a green **compile** is the primary signal.

## Code Coverage

Coverage can be collected while running the built assembly, e.g.:
```powershell
dotnet vstest $dll --collect:"Code Coverage" --ResultsDirectory:TestResults
```
(or wire up `coverlet.collector` via a runsettings file). Then generate an HTML report:
```powershell
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator -reports:**/*.cobertura.xml -targetdir:./coverage-report -reporttypes:Html
```

### Coverage Goals (aspirational, not measured)
- **Overall**: aim for > 80%
- **Critical paths**: aim for > 90%
  - Event CRUD operations
  - Audio recording / queue processing
  - RAG and similarity search
  - Export / Import operations

These are targets, not verified figures.

## Test Categories Explained

### 1. Unit / Service Tests

These verify individual components using mocked dependencies and, where a database is involved, an EF Core InMemory store created through `TestDbContextFactory.CreateInMemory()`.

**Service tests** (`Services/`) cover the factory-backed services directly:
- **EventServiceTests** — CRUD, validation (empty title, end-before-start, invalid/normalized category), date-range and search queries, counts.
- **TimelineServiceTests** — viewport creation, zoom in/out, panning, event/era loading, earliest/latest dates, track layout.
- **SettingsServiceTests** — typed get/set, existence/delete, defaults (default theme is `dark`).

**QueueServiceTests** (mock-only; no DbContext)
- ✅ Requires an `INotificationService` (constructor throws `ArgumentNullException` if null)
- ✅ Adding recordings to queue
- ✅ Retrieving queue items
- ✅ Updating queue item status
- ✅ Getting queue count by status
- ✅ Removing queue items
- ✅ Retrying failed items
- ✅ Clearing completed items
- ✅ Event raising on status change

**ExportServiceTests** (in-memory DB via `TestDbContextFactory`)
- ✅ JSON export with full event data
- ✅ JSON export with date filtering
- ✅ CSV export
- ✅ Markdown export with year organization
- ✅ Full database export (events, eras, tags)
- ✅ Progress reporting
- ✅ Special character escaping in CSV

**ImportServiceTests** (in-memory DB via `TestDbContextFactory`)
- ✅ Valid JSON import
- ✅ Duplicate detection with skip resolution
- ✅ Overwrite conflict resolution
- ✅ Create duplicate resolution
- ✅ Tag import
- ✅ Era import
- ✅ File validation
- ✅ Invalid file handling
- ✅ Progress reporting

**RagServiceTests** (in-memory DB; mocked `IEmbeddingService`, `IEventService`, `ICrossReferenceRepository`)
- ✅ Finding similar events
- ✅ Similarity threshold filtering
- ✅ Tag suggestions based on similar events
- ✅ Pattern detection with/without date ranges
- ✅ Cross-reference detection (heuristic — temporal proximity / shared category; no LLM) persisted via `ICrossReferenceRepository`
- ✅ Non-existent event handling
- ✅ Embedding similarity calculation

### 2. Integration Tests

Integration tests verify components working together with real database operations.

**DatabaseIntegrationTests** (real SQLite **file** DB via `TestDbContextFactory.CreateSqliteFile`)
- ✅ Database connection
- ✅ Full CRUD operations
- ✅ Relationships (events with eras and tags)
- ✅ Pagination (25 events, 3 pages)
- ✅ Date range queries
- ✅ Search functionality
- ✅ Category filtering
- ✅ Transaction rollback on errors
- ✅ Concurrent write operations (10 simultaneous)

### 3. Performance Tests

Performance tests ensure the application meets scalability requirements.

**PerformanceTests** (real SQLite **file** DB via `TestDbContextFactory.CreateSqliteFile`)

**Load Tests:**
- ✅ Create 5000 events (< 10 seconds)
- ✅ Query 5000 events (< 2 seconds)
- ✅ Pagination with 5000 events (< 500ms)
- ✅ Date range query on 5000 events (< 1 second)
- ✅ Search on 5000 events (< 1.5 seconds)
- ✅ Category filter on 5000 events (< 1 second)
- ✅ Update 100 events (< 5 seconds)

**Memory Test:**
- ✅ Load 5000 events in memory (< 100 MB with AsNoTracking)

**Concurrency Test:**
- ✅ 20 simultaneous read operations (< 5 seconds)

**Bulk Operations:**
- ✅ Delete 500 events (< 3 seconds)

## Performance Benchmarks

The values below are the **assertion thresholds** encoded in `PerformanceTests` (i.e. the upper bounds each test asserts against on a SQLite file DB). They are budgets the tests enforce, **not** measured/verified results — actual timings vary by machine and are written to test output via `ITestOutputHelper`.

| Operation | Asserted upper bound |
|-----------|----------------------|
| Create 5000 events | < 10s |
| Query all 5000 events | < 2s |
| Paginated query (page 1, size 50) | < 500ms |
| Date range query (5000 events) | < 1s |
| Search (5000 events) | < 1.5s |
| Category filter (5000 events) | < 1s |
| Update 100 events | < 5s |
| Memory for 5000 events (AsNoTracking) | < 100 MB |
| 20 concurrent reads | < 5s |
| Delete 500 events | < 3s |

### Database Optimizations
- `AsNoTracking()` on all read-only queries
- Pagination for large datasets
- Indexed columns for common queries
- Connection pooling
- Compiled queries for frequent operations

## Test Data Generators

### Event Generator (Performance Tests)
```csharp
GenerateTestEvents(int count)
```
- Creates realistic test events with varied:
  - Categories (Work, Personal, Education, Health, etc.)
  - Date ranges (5 years)
  - Locations
  - Confidence scores
- Fixed seed (42) for reproducibility

### Embedding Generator (RAG Tests)
```csharp
GenerateMockEmbedding(int seed)
```
- Creates 1536-dimension float arrays
- Seeded for consistent test results
- Simulates OpenAI embedding vectors

## Continuous Integration

### GitHub Actions — `.github/workflows/windows-native-build.yml`
The real workflow runs on **`windows-latest`** and:
- Installs the **.NET 8 SDK** plus **Visual Studio MSBuild** (`microsoft/setup-msbuild`).
- Builds the whole solution **`Release | x64`** with **`msbuild MemoryTimeline.sln /t:Restore,Build`** (not `dotnet build`).
- Runs tests with **`dotnet vstest`** against the built `MemoryTimeline.Tests.dll` (not `dotnet test`).

### Behavior
- **Compilation is the gate.** A build failure fails the workflow.
- The headless VSTest run of the self-contained WinUI test assembly is **best-effort** (`continue-on-error: true`); its `.trx`/logs are uploaded as artifacts for visibility but do not by themselves fail the run.
- There is currently **no** Codecov upload or long-term benchmark tracking configured.

## Writing New Tests

### Unit Test Template
```csharp
using FluentAssertions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

public class MyServiceTests : IDisposable
{
    private readonly Mock<IDependency> _dependencyMock;
    private readonly MyService _service;

    public MyServiceTests()
    {
        _dependencyMock = new Mock<IDependency>();
        _service = new MyService(_dependencyMock.Object);
    }

    [Fact]
    public async Task MethodName_Scenario_ExpectedResult()
    {
        // Arrange
        var input = "test";
        _dependencyMock.Setup(d => d.DoSomething(input))
            .ReturnsAsync("result");

        // Act
        var result = await _service.MethodToTest(input);

        // Assert
        result.Should().Be("result");
        _dependencyMock.Verify(d => d.DoSomething(input), Times.Once);
    }

    public void Dispose()
    {
        // Cleanup
    }
}
```

### Integration Test Template
```csharp
public class MyIntegrationTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly string _databasePath;

    public MyIntegrationTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"IntegrationTest_{Guid.NewGuid()}.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Integration_Test_Name()
    {
        // Test with real database
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
```

### Performance Test Template
```csharp
public class MyPerformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Stopwatch _stopwatch;

    public MyPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _stopwatch = new Stopwatch();
    }

    [Fact]
    public async Task PerformanceTest_LargeDataset_CompletesQuickly()
    {
        // Arrange
        var largeDataset = GenerateTestData(10000);

        // Act
        _stopwatch.Start();
        var result = await ProcessLargeDataset(largeDataset);
        _stopwatch.Stop();

        // Assert
        _output.WriteLine($"Processed {largeDataset.Count} items in {_stopwatch.ElapsedMilliseconds}ms");
        _stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    public void Dispose()
    {
        // Cleanup
    }
}
```

## Best Practices

### Test Naming
- Use pattern: `MethodName_Scenario_ExpectedResult`
- Examples:
  - `CreateEvent_ValidData_ReturnsEventWithId`
  - `DeleteEvent_NonExistentId_ThrowsNotFoundException`
  - `SearchEvents_EmptyQuery_ReturnsAllEvents`

### Arrange-Act-Assert Pattern
```csharp
[Fact]
public async Task Example_Test()
{
    // Arrange - Set up test data and mocks
    var input = "test";
    _mock.Setup(m => m.Method(input)).Returns("result");

    // Act - Execute the method under test
    var result = await _service.Method(input);

    // Assert - Verify the results
    result.Should().Be("result");
    _mock.Verify(m => m.Method(input), Times.Once);
}
```

### Fluent Assertions
```csharp
// Prefer FluentAssertions over xUnit assertions
result.Should().NotBeNull();
result.Should().Be(expected);
result.Should().BeEquivalentTo(expected);
collection.Should().HaveCount(5);
collection.Should().Contain(item);
value.Should().BeGreaterThan(10);
exception.Should().BeOfType<ArgumentException>();
```

### Async Tests
```csharp
// Always use async/await for async tests
[Fact]
public async Task AsyncTest()
{
    var result = await _service.MethodAsync();
    result.Should().NotBeNull();
}
```

### Test Data Cleanup
```csharp
public class MyTests : IDisposable
{
    // Use IDisposable for cleanup
    public void Dispose()
    {
        _context?.Dispose();
        DeleteTestFiles();
    }
}
```

## Troubleshooting Tests

### Tests Fail Intermittently
- Check for timing issues in async code
- Use `Task.Delay()` carefully
- Ensure proper disposal of resources
- Check for shared state between tests

### Database Lock Issues
- Ensure each test uses unique database
- Properly dispose DbContext
- Use `Database.EnsureDeleted()` in Dispose

### Performance Tests Failing
- Run on dedicated hardware (not during heavy load)
- Check system resources (CPU, memory, disk)
- Review recent code changes for regressions
- Adjust thresholds if consistently failing

### Coverage Not Generating
```powershell
# Collect coverage against the already-built test assembly (do NOT use `dotnet test`)
dotnet vstest $dll --collect:"Code Coverage" --ResultsDirectory:TestResults

# Check for coverage files in TestResults/
dir -Recurse -Filter "*.coverage"
dir -Recurse -Filter "*.cobertura.xml"
```

## Test Statistics

Approximate current suite (as of Phase 7):
- **Service tests** (`Services/`): EventService, TimelineService, SettingsService
- **Unit tests** (`UnitTests/`): Queue, Export, Import, Rag
- **Model tests** (`Models/`): layout engine, viewport, scale
- **Integration tests** (`Integration/`): ~9 database tests on a real SQLite file DB
- **Performance tests** (`Performance/`): ~10 load/scalability tests
- **Coverage**: target > 80% (aspirational, not measured)

### Migration status of the test project
All service-backed test classes have been **migrated to the `IDbContextFactory<AppDbContext>` pattern** and construct services with their **current** signatures — for example `new EventService(_repository, _contextFactory, logger)`, `new ExportService(_contextFactory, logger)`, `new ImportService(_contextFactory, logger)`, and `new RagService(embeddingService, eventService, _contextFactory, crossReferenceRepository, logger)` (RagService no longer takes an `ILlmService` or a raw `AppDbContext`). No test file uses the older `(_repository, _context, …)` / `(_context, …)` constructor signatures. As of this writing the **only** build blocker for the solution is the WinUI app's XAML/PRI resource-generation step failing under the `dotnet` build engine — the `MemoryTimeline.Tests` project fails to build **only as a downstream consequence** of the app project failing, not because of stale test code. Building with Visual Studio `msbuild.exe` (as CI does) is the supported path.

---

**Last Updated**: 2026-07-10
