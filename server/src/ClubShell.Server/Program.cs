using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
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
var clubOptions = builder.Configuration.GetSection("Club").Get<ClubOptions>() ?? new ClubOptions();
var proxyOptions = builder.Configuration.GetSection("Proxy").Get<ProxyOptions>() ?? new ProxyOptions();
var agentOptions = builder.Configuration.GetSection("Agents").Get<AgentOptions>() ?? new AgentOptions();
var sessionOptions = builder.Configuration.GetSection("Sessions").Get<SessionsOptions>() ?? new SessionsOptions();
var realtimeOptions = builder.Configuration.GetSection("Realtime").Get<RealtimeOptions>() ?? new RealtimeOptions();

// Policy seed (D-14): data/policy.json when the operator put one there, else the example policy shipped with the build.
var policySeedPath = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Catalog:PolicySeedPath"] ?? "data/policy.json");
if (!File.Exists(policySeedPath))
{
    policySeedPath = Path.Combine(AppContext.BaseDirectory, "seed", "policies.example.json");
}
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
builder.Services.AddClubDatabase(connectionString);
builder.Services.AddSingleton<ClubRepository>();
builder.Services.AddSingleton<PcRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ReplayLog>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<CommandRepository>();
builder.Services.AddSingleton<AgentSocketHub>();
builder.Services.AddSingleton<CommandDispatcher>();
if (builder.Configuration.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService<HubLock>();
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

if (builder.Configuration.GetValue("Database:MigrateOnStart", true))
{
    await Database.MigrateAsync(app.Services);
}

var clubs = app.Services.GetRequiredService<ClubRepository>();
await clubs.EnsureAsync(clubOptions);
await clubs.SeedPolicyAsync(JsonDefaults.Deserialize<Policy>(File.ReadAllText(policySeedPath))
    ?? throw new InvalidOperationException($"Policy seed {policySeedPath} is empty"));

if (proxyOptions.ClientIpHeader.Length > 0)
{
    app.UseMiddleware<ClientIpMiddleware>();
}
else if (proxyOptions.Trusted.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<ApiErrorMiddleware>();
app.UseWebSockets();
app.UseRouting();
app.UseMiddleware<AgentAuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAgentEndpoints();
app.Map("/ws/agent", (HttpContext context, AgentSocketHub hub) => hub.HandleAsync(context));
app.MapNotImplemented(ContractStatus.Load(contractPath), ContractStatus.Implemented);
app.MapFallback("/api/v1/{**route}", context =>
        throw new ApiException(StatusCodes.Status404NotFound, ErrorCode.NotFound, "Route not found", new { route = context.Request.Path.Value }))
    .WithMetadata(new AuthRequirement(AuthMode.None));

app.Run();

/// <summary>Entry point for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
