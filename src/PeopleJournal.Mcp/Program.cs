using PeopleJournal.Mcp.Identity;
using PeopleJournal.Mcp.Journal;
using PeopleJournal.Mcp.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(auth);
builder.Services.Configure<JournalOptions>(builder.Configuration.GetSection("Journal"));
builder.Services.AddSingleton<IUserContextResolver, UserContextResolver>();
builder.Services.AddHttpContextAccessor();
var journalOptions = builder.Configuration.GetSection("Journal").Get<JournalOptions>() ?? new JournalOptions();
if (journalOptions.Store == JournalStoreKind.Json)
{
    builder.Services.AddSingleton<IJournalStore, JsonJournalStore>();
}
else
{
    builder.Services.AddSingleton<IJournalStore, FileJournalStore>();
}

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<JournalTools>();

if (auth.Mode == AuthMode.EntraId)
{
    // Step 5: every request must carry an Entra ID access token issued for this API.
    var authority = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0";

    builder.Services.AddAuthentication(options =>
        {
            options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.MapInboundClaims = false; // keep short claim names like "oid" and "tid"
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = authority,
                ValidateAudience = true,
                ValidAudiences = [auth.ClientId, $"api://{auth.ClientId}"],
                ValidateLifetime = true,
                NameClaimType = "name",
            };
        })
        .AddMcp(options =>
        {
            // Published at /.well-known/oauth-protected-resource so MCP clients
            // can discover where to sign in.
            options.ResourceMetadata = new()
            {
                AuthorizationServers = { authority },
                ScopesSupported = [$"api://{auth.ClientId}/{auth.Scope}"],
            };
        });

    builder.Services.AddAuthorization();
}

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", authMode = auth.Mode.ToString(), store = journalOptions.Store.ToString() }));

if (auth.Mode == AuthMode.EntraId)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcp("/mcp").RequireAuthorization();
}
else
{
    app.Logger.LogWarning(
        "Auth mode is None: every caller is treated as '{DevUser}'. Only expose this on localhost.",
        auth.DevUserId);
    app.MapMcp("/mcp");
}

app.Run();
