using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Admin;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Games;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using ClubShell.Server.Updates;
using ClubShell.Server.Users;
using ClubShell.Server.Wallet;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Railway passes the port in PORT; otherwise ASPNETCORE_HTTP_PORTS (8080 in the aspnet image) or --urls apply.
if (builder.Configuration["PORT"] is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var connectionString = builder.Configuration.GetConnectionString("Club")
    ?? throw new InvalidOperationException("ConnectionStrings:Club is not configured");
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
authOptions.SigningKeyPath = Path.Combine(builder.Environment.ContentRootPath, authOptions.SigningKeyPath);
authOptions.PepperPath = Path.Combine(builder.Environment.ContentRootPath, authOptions.PepperPath);
authOptions.PinAttempts = builder.Configuration.GetValue("RateLimit:PinAttempts", authOptions.PinAttempts);
authOptions.PinWindowSec = builder.Configuration.GetValue("RateLimit:PinWindowSec", authOptions.PinWindowSec);
var clubOptions = builder.Configuration.GetSection("Club").Get<ClubOptions>() ?? new ClubOptions();
var proxyOptions = builder.Configuration.GetSection("Proxy").Get<ProxyOptions>() ?? new ProxyOptions();
var agentOptions = builder.Configuration.GetSection("Agents").Get<AgentOptions>() ?? new AgentOptions();
var sessionOptions = builder.Configuration.GetSection("Sessions").Get<SessionsOptions>() ?? new SessionsOptions();
var realtimeOptions = builder.Configuration.GetSection("Realtime").Get<RealtimeOptions>() ?? new RealtimeOptions();
var corsOptions = builder.Configuration.GetSection("Cors").Get<CorsOptions>() ?? new CorsOptions();
var maintenanceOptions = builder.Configuration.GetSection("Maintenance").Get<MaintenanceOptions>() ?? new MaintenanceOptions();

// Policy seed (D-14): data/policy.json when the operator put one there, else the example policy shipped with the build.
var policySeedPath = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Catalog:PolicySeedPath"] ?? "data/policy.json");
if (!File.Exists(policySeedPath))
{
    policySeedPath = Path.Combine(AppContext.BaseDirectory, "seed", "policies.example.json");
}
// Games seed (D-14): data/games.json; without it the catalog is left as it is.
var gamesSeedPath = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Catalog:GamesSeedPath"] ?? "data/games.json");
// Products seed (D-14): data/products.json; without it the products are left as they are.
var productsSeedPath = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Catalog:ProductsSeedPath"] ?? "data/products.json");
var contractPath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Contracts:OpenApiPath"] ?? "contracts/openapi.yaml");

builder.Services.ConfigureHttpJsonOptions(o => ServerJson.Apply(o.SerializerOptions));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton(clubOptions);
builder.Services.AddSingleton(proxyOptions);
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton(sessionOptions);
builder.Services.AddSingleton(realtimeOptions);
builder.Services.AddSingleton(corsOptions);
builder.Services.AddSingleton(maintenanceOptions);
builder.Services.AddClubDatabase(connectionString);
builder.Services.AddSingleton<ClubRepository>();
builder.Services.AddSingleton<PcRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ReplayLog>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<CommandRepository>();
builder.Services.AddSingleton<AgentSocketHub>();
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<UserTokens>();
builder.Services.AddSingleton<StaffTokens>();
builder.Services.AddSingleton<Pushes>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<AutomationService>();
builder.Services.AddSingleton<WebhookClient>();
builder.Services.AddSingleton<SessionTickWorker>();
builder.Services.AddSingleton<PcStatusWorker>();
builder.Services.AddSingleton<HealthWorker>();
builder.Services.AddSingleton<ClubTickWorker>();
builder.Services.AddSingleton<WebhookWorker>();
builder.Services.AddSingleton<MaintenanceWorker>();
var workers = builder.Configuration.GetValue("Workers:Enabled", true);
if (workers)
{
    builder.Services.AddSingleton<HubLock>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionTickWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PcStatusWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ClubTickWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MaintenanceWorker>());
}
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var cidr in proxyOptions.Trusted)
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

var app = builder.Build();

// Single-instance guard (DESIGN §6.8) before anything below writes shared state: migrations, enrollment key, policy seed.
if (workers)
{
    await app.Services.GetRequiredService<HubLock>().AcquireAsync();
}

if (builder.Configuration.GetValue("Database:MigrateOnStart", true))
{
    await Database.MigrateAsync(app.Services);
}

var clubs = app.Services.GetRequiredService<ClubRepository>();
await clubs.EnsureAsync(clubOptions);
await clubs.SeedPolicyAsync(JsonDefaults.Deserialize<Policy>(File.ReadAllText(policySeedPath))
    ?? throw new InvalidOperationException($"Policy seed {policySeedPath} is empty"));
await CatalogSeed.ApplyAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), gamesSeedPath, app.Services.GetRequiredService<TimeProvider>());
await ProductSeed.ApplyAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), productsSeedPath, app.Services.GetRequiredService<TimeProvider>());
var staffTokens = app.Services.GetRequiredService<StaffTokens>();
if (builder.Configuration.GetValue("Seed:Dev", false))
{
    await DevSeed.SeedAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), app.Services.GetRequiredService<TimeProvider>(), staffTokens,
        seats: builder.Configuration.GetValue("Seed:DevPcs", false));
}

// After the dev seed: its staff fills the table, so no owner PIN is generated in Development.
await staffTokens.EnsureOwnerAsync(builder.Configuration["Club:OwnerPin"]);

if (proxyOptions.ClientIpHeader.Length > 0)
{
    app.UseMiddleware<ClientIpMiddleware>();
}
else if (proxyOptions.Trusted.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<ApiErrorMiddleware>();
app.UseMiddleware<CorsMiddleware>();
app.UseWebSockets();
app.UseRouting();
app.UseMiddleware<AgentAuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAgentEndpoints();
app.MapPlayerAuthEndpoints();
app.MapUserEndpoints();
app.MapSessionEndpoints();
app.MapWalletEndpoints();
app.MapGameEndpoints();
app.MapUpdateEndpoints();
app.MapPcEndpoints();
app.MapStaffEndpoints();
app.MapCounterEndpoints();
app.MapShiftEndpoints();
app.MapPcAdminEndpoints();
app.MapStaffAdminEndpoints();
app.MapClientEndpoints();
app.MapPromoEndpoints();
app.MapTariffEndpoints();
app.MapStockEndpoints();
app.MapClubSettingsEndpoints();
app.MapCatalogAdminEndpoints();
app.MapHealthEndpoints();
app.MapControlEndpoints();
app.MapReportsEndpoints();
app.MapNetworkEndpoints();
app.Map("/ws/agent", (HttpContext context, AgentSocketHub hub) => hub.HandleAsync(context));
app.MapNotImplemented(ContractStatus.Load(contractPath), ContractStatus.Implemented);
app.MapFallback("/api/v1/{**route}", context =>
        throw new ApiException(StatusCodes.Status404NotFound, ErrorCode.NotFound, "Route not found", new { route = context.Request.Path.Value }))
    .WithMetadata(new AuthRequirement(AuthMode.None));

app.Run();

/// <summary>Entry point for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
