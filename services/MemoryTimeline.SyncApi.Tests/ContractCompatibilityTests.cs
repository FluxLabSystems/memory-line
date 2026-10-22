using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MemoryTimeline.SyncContracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YamlDotNet.Serialization;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The contract-compatibility gate the shared-contracts README promised
/// (design doc migration plan, Epic A): the OpenAPI document, the shipped
/// DTOs and the running service must agree, and this test run is what keeps
/// them from drifting apart.
///
/// Three things are checked against the YAML copied into the test output:
/// every <c>SyncChangeEntityType</c> constant appears in both entityType enums
/// and nothing unknown does; every public DTO in the contracts assembly maps
/// to a schema whose property names equal the DTO's camelCased properties
/// (both directions, so an added property with no schema fails as loudly as
/// a schema property with no DTO); and every path+method in the document is
/// routed by the real host while every routed endpoint is documented. A
/// fourth check ties the document's version to the changelog.
/// </summary>
public class ContractCompatibilityTests : IClassFixture<SyncApiFixture>
{
    private static readonly string ContractsDirectory = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly string YamlPath = Path.Combine(ContractsDirectory, "memory-line-sync-v1.yaml");
    private static readonly string ReadmePath = Path.Combine(ContractsDirectory, "README.md");

    /// <summary>Base path the YAML's <c>servers</c> entry declares; every routed path carries it.</summary>
    private const string BasePath = "/api/v1";

    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    /// <summary>
    /// DTO type → schema name. Most names coincide; the exceptions are the
    /// response DTOs the YAML names after the resource (Capture, DeviceInfo,
    /// Trip) and the route context, whose C# name avoids ASP.NET Core's own
    /// RouteContext type. A DTO missing from this map fails the test, so a new
    /// DTO cannot ship without a schema.
    /// </summary>
    private static readonly Dictionary<Type, string> SchemaByDto = new()
    {
        [typeof(ApiError)] = "Error",
        [typeof(CaptureCreateRequest)] = "CaptureCreateRequest",
        [typeof(LocationContext)] = "LocationContext",
        [typeof(CaptureResponse)] = "Capture",
        [typeof(ArtifactInitiateRequest)] = "ArtifactInitiateRequest",
        [typeof(ArtifactInitiateResponse)] = "ArtifactInitiateResponse",
        [typeof(ArtifactCompleteRequest)] = "ArtifactCompleteRequest",
        [typeof(ArtifactCompleteResponse)] = "ArtifactCompleteResponse",
        [typeof(ArtifactSummary)] = "ArtifactSummary",
        [typeof(SyncChangeDto)] = "SyncChange",
        [typeof(SyncPullResponse)] = "SyncPullResponse",
        [typeof(SyncPushRequest)] = "SyncPushRequest",
        [typeof(SyncPushEntry)] = "SyncPushEntry",
        [typeof(SyncPushResponse)] = "SyncPushResponse",
        [typeof(SyncPushEntryResult)] = "SyncPushEntryResult",
        [typeof(SyncAckRequest)] = "SyncAckRequest",
        [typeof(CaptureChangePayload)] = "CaptureChangePayload",
        [typeof(CaptureStatusChangePayload)] = "CaptureStatusChangePayload",
        [typeof(DeviceRegisterRequest)] = "DeviceRegisterRequest",
        [typeof(DeviceRegisterResponse)] = "DeviceRegisterResponse",
        [typeof(TokenRefreshRequest)] = "TokenRefreshRequest",
        [typeof(TokenRefreshResponse)] = "TokenRefreshResponse",
        [typeof(DeviceInfoResponse)] = "DeviceInfo",
        [typeof(AssistantSessionCreateRequest)] = "AssistantSessionCreateRequest",
        [typeof(AssistantSessionResponse)] = "AssistantSessionResponse",
        [typeof(AssistantTurnCreateRequest)] = "AssistantTurnCreateRequest",
        [typeof(AssistantContextItem)] = "AssistantContextItem",
        [typeof(AssistantTurnResponse)] = "AssistantTurnResponse",
        [typeof(AssistantTurnResult)] = "AssistantTurnResult",
        [typeof(AssistantCitation)] = "AssistantCitation",
        [typeof(AssistantTurnChangePayload)] = "AssistantTurnChangePayload",
        [typeof(AssistantTurnChunkPayload)] = "AssistantTurnChunkPayload",
        [typeof(EventProjectionPayload)] = "EventProjectionPayload",
        [typeof(EraProjectionPayload)] = "EraProjectionPayload",
        [typeof(PersonProjectionPayload)] = "PersonProjectionPayload",
        [typeof(PendingEventProjectionPayload)] = "PendingEventProjectionPayload",
        [typeof(PendingEventDecisionPayload)] = "PendingEventDecisionPayload",
        [typeof(PendingEventCorrections)] = "PendingEventCorrections",
        [typeof(TripCreateRequest)] = "TripCreateRequest",
        [typeof(TripResponse)] = "Trip",
        [typeof(TripRouteContext)] = "RouteContext",
        [typeof(DetourSuggestion)] = "DetourSuggestion",
    };

    private readonly SyncApiFixture _fixture;

    public ContractCompatibilityTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void Document_IsOpenApi3_AndItsVersionHasAChangelogEntry()
    {
        var document = LoadDocument();
        document["openapi"].Should().BeOfType<string>().Which.Should().StartWith("3.0");

        var version = Map(document["info"])["version"].Should().BeOfType<string>().Which;
        Regex.IsMatch(version, @"^\d+\.\d+\.\d+$").Should().BeTrue($"info.version '{version}' must be semver");

        var majorMinor = string.Join('.', version.Split('.').Take(2));
        File.ReadAllText(ReadmePath).Should().Contain(
            $"### v{majorMinor} ",
            $"the shared-contracts README changelog must carry an entry for v{majorMinor}");
    }

    [Fact]
    public void EntityTypeEnums_MatchTheSyncChangeEntityTypeConstants_Exactly()
    {
        var constants = typeof(SyncChangeEntityType)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        constants.Should().NotBeEmpty();

        var schemas = Schemas();
        foreach (var schemaName in new[] { "SyncChange", "SyncPushEntry" })
        {
            var entityType = Map(Map(Map(schemas[schemaName])["properties"])["entityType"]);
            var enumValues = List(entityType["enum"]).Select(v => (string)v!).ToHashSet(StringComparer.Ordinal);

            enumValues.Should().BeEquivalentTo(
                constants,
                $"{schemaName}.entityType must list exactly the SyncChangeEntityType constants " +
                "(a new entity type is added to the DTO and the YAML in the same commit)");
        }
    }

    [Fact]
    public void EveryDto_HasASchemaWithTheSamePropertyNames()
    {
        var schemas = Schemas();
        var contractTypes = typeof(ApiError).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && !(t.IsAbstract && t.IsSealed))
            .ToList();

        var unmapped = contractTypes.Where(t => !SchemaByDto.ContainsKey(t)).Select(t => t.Name).ToList();
        unmapped.Should().BeEmpty(
            "every public DTO in MemoryTimeline.SyncContracts must be mapped to an OpenAPI schema in this test");

        foreach (var (dto, schemaName) in SchemaByDto)
        {
            schemas.Should().ContainKey(schemaName, $"the YAML must define a schema for {dto.Name}");
            var schema = Map(schemas[schemaName]);
            schema.Should().ContainKey("properties", $"schema {schemaName} must list its properties");

            var documented = Map(schema["properties"]).Keys.Select(k => (string)k).ToHashSet(StringComparer.Ordinal);
            var declared = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                .ToHashSet(StringComparer.Ordinal);

            documented.Should().BeEquivalentTo(
                declared,
                $"schema {schemaName} must mirror {dto.Name} property for property " +
                "(camelCase, as System.Text.Json web defaults serialize it)");
        }
    }

    [Fact]
    public void EveryDocumentedOperation_IsRouted_AndEveryRouteIsDocumented()
    {
        var document = LoadDocument();
        var documented = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pathKey, pathItem) in Map(document["paths"]))
        {
            var path = (string)pathKey;
            foreach (var (methodKey, _) in Map(pathItem))
            {
                var method = (string)methodKey;
                if (HttpMethods.Contains(method))
                {
                    documented.Add($"{method.ToUpperInvariant()} {BasePath}{path}");
                }
            }
        }

        var routed = new HashSet<string>(StringComparer.Ordinal);
        var dataSource = _fixture.Services.GetRequiredService<EndpointDataSource>();
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            var template = Normalize(endpoint.RoutePattern);
            foreach (var method in methods)
            {
                routed.Add($"{method.ToUpperInvariant()} {template}");
            }
        }

        routed.Should().NotBeEmpty();
        documented.Except(routed).Should().BeEmpty(
            "every operation the contract documents must be served by the host (it would 404 otherwise)");
        routed.Except(documented).Should().BeEmpty(
            "every route the host serves must be documented in the contract");
    }

    /// <summary>
    /// Rebuilds a route template from its segments so parameter constraints
    /// ("{partNumber:int}") and casing cannot make a routed path look
    /// different from the documented one.
    /// </summary>
    private static string Normalize(RoutePattern pattern)
    {
        var segments = pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart parameter => $"{{{parameter.Name}}}",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => string.Empty,
        })));
        return "/" + string.Join('/', segments);
    }

    private static Dictionary<object, object> LoadDocument()
    {
        File.Exists(YamlPath).Should().BeTrue($"the test project links the contract into its output at {YamlPath}");
        using var reader = new StreamReader(YamlPath);
        var document = new DeserializerBuilder().Build().Deserialize<object>(reader);
        return Map(document);
    }

    private static Dictionary<object, object> Schemas()
        => Map(Map(LoadDocument()["components"])["schemas"]);

    private static Dictionary<object, object> Map(object? node)
    {
        node.Should().BeOfType<Dictionary<object, object>>("this YAML node must be a mapping");
        return (Dictionary<object, object>)node!;
    }

    private static List<object> List(object? node)
    {
        node.Should().BeOfType<List<object>>("this YAML node must be a sequence");
        return (List<object>)node!;
    }
}
