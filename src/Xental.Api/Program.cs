using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// JWT bearer authentication (client-credentials tokens).
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
});

// Health checks.
builder.Services.AddHealthChecks();

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
        Description = "Xental backend API."
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

// Apply EF Core migrations on startup for the relational (Postgres) provider so a
// freshly-provisioned environment gets its schema automatically. Skipped for the
// SQLite test host, which creates the schema itself.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<XentalDbContext>();
    if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        db.Database.Migrate();
}

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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Exposed so the integration test host (WebApplicationFactory<Program>) can boot the API.
public partial class Program { }
