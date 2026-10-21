using MemoryTimeline.SyncApi.Api;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// .NET 10 changed Microsoft.Data.Sqlite's timezone handling (offset-less stored
// timestamps now read as UTC rather than local). Every timestamp in an existing
// sync.db was written under the old rules, so keep them until the stored values
// are audited — see the Windows app's Program.cs for the same switch.
AppContext.SetSwitch("Microsoft.Data.Sqlite.Pre10TimeZoneHandling", true);

var builder = WebApplication.CreateBuilder(args);

// Bootstrap runs before the host is built: the signing key must exist for JWT
// setup and the data directory for the SQLite connection string.
var syncOptions = builder.Configuration.GetSection(SyncApiOptions.SectionName).Get<SyncApiOptions>()
    ?? new SyncApiOptions();
var environment = SyncApiBootstrapper.Initialize(syncOptions);

builder.Services.Configure<SyncApiOptions>(builder.Configuration.GetSection(SyncApiOptions.SectionName));
builder.Services.AddSingleton(environment);

// One short-lived context per operation via the factory; services are stateless
// singletons over it (same pattern as the Windows app, see claude.md).
builder.Services.AddDbContextFactory<SyncDbContext>(
    options => options.UseSqlite($"Data Source={environment.DatabasePath}"));

builder.Services.AddSingleton<ISyncTokenService, SyncTokenService>();
builder.Services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
builder.Services.AddSingleton<IDeviceService, DeviceService>();
builder.Services.AddSingleton<ICaptureService, CaptureService>();
builder.Services.AddSingleton<IArtifactService, ArtifactService>();
builder.Services.AddSingleton<ISyncChangeService, SyncChangeService>();
builder.Services.AddSingleton<IAssistantService, AssistantService>();
builder.Services.AddSingleton<IArtifactRetentionService, ArtifactRetentionService>();
builder.Services.AddSingleton<IAssistantSessionReaper, AssistantSessionReaper>();
builder.Services.AddSingleton<ITripService, TripService>();
builder.Services.AddSingleton<IDetourService, DetourService>();
// No place provider ships with the service (design §22 decision 5 names
// Google Places with an Apple failover; neither adapter exists yet), so detour
// generation is honest and yields nothing until one is registered here.
builder.Services.AddSingleton<IDetourSuggestionProvider, NoDetourSuggestionProvider>();

// Housekeeping on timers: the artifact retention / temp-file sweep (design
// §22 decision 7) and the idle assistant-session reaper (§6.6). Hosted
// services start after InitializeDatabaseAsync below has created the schema.
builder.Services.AddHostedService<RetentionSweepHostedService>();
builder.Services.AddHostedService<AssistantSessionReaperHostedService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep raw claim types so "sub" stays "sub" for DeviceAuthEndpointFilter.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = SyncTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = SyncTokenService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(environment.SigningKey),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = SyncTokenService.DeviceClaim,
        };
        options.Events = new JwtBearerEvents
        {
            // Replace the default empty 401 with the ApiError envelope all
            // error responses use (shared-contracts Error schema).
            OnChallenge = async challenge =>
            {
                challenge.HandleResponse();
                if (challenge.Response.HasStarted)
                {
                    return;
                }

                challenge.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await challenge.Response.WriteAsJsonAsync(new ApiError
                {
                    Code = SyncApiErrorCodes.Unauthorized,
                    Message = "A valid access token is required.",
                    Retryable = false,
                    CorrelationId = challenge.HttpContext.TraceIdentifier,
                });
            },
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Unhandled exceptions become 500 ApiError responses; the middleware logs the
// exception itself (with stack trace) before this handler writes the body.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new ApiError
    {
        Code = SyncApiErrorCodes.InternalError,
        Message = "Unexpected server error.",
        Retryable = true,
        CorrelationId = context.TraceIdentifier,
    });
}));

var ownerId = await SyncApiBootstrapper.InitializeDatabaseAsync(app.Services);
app.Logger.LogInformation("Sync data directory: {DataDirectory}", environment.DataDirectory);
app.Logger.LogInformation("Sync owner: {OwnerId}", ownerId);
// The one deliberate pairing-code log line (design §14.5 exception) — the
// operator needs it to pair devices. Never logged anywhere else.
app.Logger.LogInformation("Device pairing code: {PairingCode}", environment.PairingCode);

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1");
var publicApi = api.MapGroup(string.Empty);
var authenticatedApi = api.MapGroup(string.Empty)
    .RequireAuthorization()
    .AddEndpointFilter(DeviceAuthEndpointFilter.Instance);

DeviceEndpoints.MapPublicEndpoints(publicApi);
DeviceEndpoints.MapAuthenticatedEndpoints(authenticatedApi);
CaptureEndpoints.MapEndpoints(authenticatedApi);
ArtifactEndpoints.MapEndpoints(authenticatedApi);
SyncEndpoints.MapEndpoints(authenticatedApi);
AssistantEndpoints.MapEndpoints(authenticatedApi);
TripEndpoints.MapEndpoints(authenticatedApi);
DetourEndpoints.MapEndpoints(authenticatedApi);

app.Run();

/// <summary>Exposed for WebApplicationFactory&lt;Program&gt; in integration tests.</summary>
public partial class Program
{
}
