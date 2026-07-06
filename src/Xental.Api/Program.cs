using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Xental.Api.Auth;
using Xental.Api.Authorization;
using Xental.Api.Middleware;
using Xental.Application;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure;
using Xental.Infrastructure.Persistence;
using Xental.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// --- Logging (Serilog: console + rolling file to a dedicated log dir) -----

// LOG_DIRECTORY lets the container point logging at a mounted volume.
var logDirectory = builder.Configuration["LOG_DIRECTORY"]
    ?? Path.Combine(AppContext.BaseDirectory, "logs");

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logDirectory, "xental-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true));

// --- Service registration ------------------------------------------------

builder.Services.AddControllers();

// Clean Architecture layers.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Current-tenant resolution from the JWT.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IModeContext, ModeContext>();
builder.Services.AddScoped<IAdminContext, AdminContext>();

// Writes the HttpOnly+Secure dashboard session cookies.
builder.Services.AddScoped<AuthCookieWriter>();

// JWT bearer authentication. API tokens arrive as `Authorization: Bearer`; dashboard
// access tokens arrive as the HttpOnly `xnt_access` cookie (read below).
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // Fall back to the access-token cookie when there's no Authorization header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) &&
                    context.Request.Cookies.TryGetValue(AuthCookieWriter.AccessCookie, out var cookie))
                {
                    context.Token = cookie;
                }
                return Task.CompletedTask;
            },
        };
    });
// Two token planes, separated by the `scope` claim:
//   dashboard -> email/password login, manages API keys
//   api       -> client-credentials, calls the payments API
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Dashboard, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Dashboard));
    options.AddPolicy(AuthPolicies.Api, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Api));
    // Either plane: an API key OR a dashboard session may read/manage these resources.
    options.AddPolicy(AuthPolicies.ApiOrDashboard, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Api, AuthPolicies.Dashboard));
    // Admin plane: any admin, and the SuperAdmin-only subset (manage admins).
    options.AddPolicy(AuthPolicies.Admin, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Admin));
    options.AddPolicy(AuthPolicies.SuperAdmin, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Admin)
        .RequireClaim(AuthPolicies.AdminRoleClaim, nameof(Xental.Domain.Admin.AdminRole.SuperAdmin)));

    // Role-gated dashboard policies. Team members carry a "role"; the account owner is "Owner".
    options.AddPolicy(AuthPolicies.TeamManage, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Dashboard)
        .RequireClaim(AuthPolicies.RoleClaim, "Owner", "Admin"));
    options.AddPolicy(AuthPolicies.ManageKeys, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Dashboard)
        .RequireClaim(AuthPolicies.RoleClaim, "Owner", "Admin", "Developer"));
    options.AddPolicy(AuthPolicies.ManageSettings, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.ScopeClaim, AuthPolicies.Dashboard)
        .RequireClaim(AuthPolicies.RoleClaim, "Owner", "Admin"));

    // Billing mutations: an API key (integrator, no team role) OR a dashboard Owner/Admin — never a
    // dashboard Employee. Reads stay on the broader ApiOrDashboard policy.
    options.AddPolicy(AuthPolicies.ManageBilling, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => ApiOrDashboardRole(ctx, "Owner", "Admin")));

    // Provisioning / operating resources from either plane: API key OR dashboard Owner/Admin/Developer.
    options.AddPolicy(AuthPolicies.Provision, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => ApiOrDashboardRole(ctx, "Owner", "Admin", "Developer")));

    // Moving money from either plane: API key OR dashboard Owner/Admin (never Employee).
    options.AddPolicy(AuthPolicies.MovePayouts, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => ApiOrDashboardRole(ctx, "Owner", "Admin")));
});

// A hybrid gate: an API-plane token always passes; a dashboard token passes only with one of the
// given team roles. Lets one endpoint serve both the API and the dashboard without an Employee
// reaching money/provisioning actions.
static bool ApiOrDashboardRole(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext ctx, params string[] roles)
{
    var scope = ctx.User.FindFirst(AuthPolicies.ScopeClaim)?.Value;
    if (scope == AuthPolicies.Api) return true;
    if (scope == AuthPolicies.Dashboard)
        return roles.Contains(ctx.User.FindFirst(AuthPolicies.RoleClaim)?.Value);
    return false;
}

// TLS is terminated at Traefik; trust its X-Forwarded-* so the real client IP/scheme
// are used (needed for correct rate-limit partitioning and HTTPS awareness).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust X-Forwarded-* only from the reverse proxy — otherwise a forged X-Forwarded-For lets an
    // attacker rotate the rate-limit key and defeat the (login) brute-force limiter. Only accept one
    // forwarding hop, and only when the immediate peer sits on a trusted network (where Traefik runs).
    // A direct external connection (bypassing the proxy) is not trusted, so its real IP is used.
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
    foreach (var p in knownProxies)
        if (System.Net.IPAddress.TryParse(p, out var ip)) options.KnownProxies.Add(ip);
    var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>()
        ?? ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"];
    foreach (var n in knownNetworks)
        if (System.Net.IPNetwork.TryParse(n, out var network)) options.KnownIPNetworks.Add(network);
});

// Rate limiting: a per-IP global safety net plus a stricter "auth" policy on the
// credential/session endpoints (login, register, token, refresh, reset, OAuth).
var rateLimitingDisabled = builder.Configuration.GetValue<bool>("RateLimiting:Disabled");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string ClientKey(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    RateLimitPartition<string> Window(HttpContext ctx, int permit) => rateLimitingDisabled
        ? RateLimitPartition.GetNoLimiter("disabled")
        : RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1) });

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => Window(ctx, 300));
    options.AddPolicy("auth", ctx => Window(ctx, 10));
    // Inbound provider webhooks: unauthenticated but signature-verified. A generous per-IP ceiling that
    // tolerates Nomba retry bursts while capping an unauthenticated CPU/parse DoS amplifier.
    options.AddPolicy("webhook", ctx => Window(ctx, 1200));

    // API-plane throttle keyed by the API KEY (or tenant), not just IP: each credential gets its
    // own quota, and live keys get a higher ceiling than sandbox (test) keys.
    options.AddPolicy("api-key", ctx =>
    {
        if (rateLimitingDisabled) return RateLimitPartition.GetNoLimiter<string>("disabled");
        var user = ctx.User;
        var key = user.FindFirst("kid")?.Value ?? user.FindFirst("tenant_id")?.Value ?? ClientKey(ctx);
        var permit = user.FindFirst("key_mode")?.Value == "live" ? 600 : 120; // per minute
        return RateLimitPartition.GetFixedWindowLimiter($"apikey:{key}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1) });
    });
});

// CORS: allow the dashboard frontend origins to call the API with credentials
// (needed so the browser sends the HttpOnly session cookies cross-origin).
// Configure Cors:AllowedOrigins (comma-separated or array); empty = same-origin only.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? (builder.Configuration["Cors:AllowedOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    ?? [];
builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
{
    if (corsOrigins.Length > 0)
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// Health checks: liveness (/health, always) + readiness (/ready, DB-gated).
builder.Services.AddHealthChecks()
    .AddCheck<Xental.Api.Health.DatabaseReadyCheck>("database", tags: ["ready"]);

// --- OpenTelemetry (metrics + traces via OTLP) ---------------------------
// Enabled only when an OTLP endpoint is configured (set by the deploy when a
// monitoring host exists). Service name comes from OTEL_SERVICE_NAME env.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter())
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

// --- OpenTelemetry (metrics + traces via OTLP) ---------------------------
// Enabled only when an OTLP endpoint is configured (set by the deploy when a
// monitoring host exists). Service name comes from OTEL_SERVICE_NAME env.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter())
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

// Swagger / OpenAPI.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Xental API",
        Version = "v1",
        Description = """
            **Xental** — reusable dedicated virtual accounts + automatic reconciliation on Nomba.

            ## Quickstart
            1. Register a developer account and verify your email at the dashboard.
            2. Create an API key (Dashboard → API keys) — copy the client secret, shown once.
            3. Exchange it for an API token: `POST /api/v1/auth/token` with `{clientId, clientSecret}`.
            4. Send `Authorization: Bearer <token>` on every payments call.
            5. Create a virtual account: `POST /api/v1/virtual-accounts` (optionally with `expectedAmountKobo`).
            6. Fund it — the Nomba webhook drives reconciliation; poll `GET /api/v1/transactions` or subscribe to outbound webhooks.
            7. Configure settlement (Dashboard → `PUT /api/v1/settings/settlement`) to auto-sweep collected funds to your bank.

            ## Auth planes
            - **Dashboard** (cookie session): developer login, API keys, settings, insights, webhook endpoints.
            - **API** (bearer token): virtual accounts, transactions, transfers.

            All money is integer **kobo**. Provider (Nomba) inflow webhooks arrive at `POST /webhooks/nomba`.
            """,
        Contact = new OpenApiContact { Name = "Xental", Url = new Uri("https://xental.online") },
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT from POST /api/v1/auth/token.",
    });
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer")] = new List<string>(),
    });

    // Surface the controller/action XML doc comments in the Swagger UI.
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});

var app = builder.Build();

// Honor Traefik's forwarded headers first (real client IP + scheme) before anything
// that depends on them (rate limiting, HTTPS awareness, logging).
app.UseForwardedHeaders();

// Apply EF Core migrations on startup for the relational (Postgres) provider so a
// freshly-provisioned environment gets its schema automatically. Skipped for the
// SQLite test host, which creates the schema itself.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<XentalDbContext>();
    if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        db.Database.Migrate();

    // One-time bootstrap of the first SuperAdmin from config (Admin:BootstrapEmail/Password).
    // Only seeds when no admin exists yet; rotate the bootstrap password after first login.
    var bootEmail = app.Configuration["Admin:BootstrapEmail"];
    var bootPassword = app.Configuration["Admin:BootstrapPassword"];
    if (!string.IsNullOrWhiteSpace(bootEmail) && !string.IsNullOrWhiteSpace(bootPassword) && !db.AdminUsers.Any())
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.AdminUsers.Add(new Xental.Domain.Admin.AdminUser(bootEmail, hasher.Hash(bootPassword), Xental.Domain.Admin.AdminRole.SuperAdmin));
        db.SaveChanges();
    }
}

// Baseline security response headers on every response (skips the Swagger UI).
app.UseMiddleware<SecurityHeadersMiddleware>();

// Log each HTTP request through Serilog.
app.UseSerilogRequestLogging();

// Translate application exceptions to ProblemDetails.
app.UseMiddleware<ExceptionHandlingMiddleware>();

// --- HTTP request pipeline ----------------------------------------------

// Swagger is enabled in every environment so the API is always explorable.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Xental API v1");
    options.DocumentTitle = "Xental API";
});

// In containers TLS is terminated upstream (proxy/ingress), so only redirect
// to HTTPS when running directly on a host.
var runningInContainer =
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
if (!runningInContainer)
{
    app.UseHttpsRedirection();
}

app.UseCors("frontend");

app.UseAuthentication();
app.UseAuthorization();

// Enforce rate-limit policies on the endpoints that opt in via [EnableRateLimiting].
app.UseRateLimiter();

app.MapControllers();
// Liveness: process is up. Readiness: DB reachable (LB gates traffic on this).
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.Run();

// Exposed so the integration test host (WebApplicationFactory<Program>) can boot the API.
public partial class Program { }
