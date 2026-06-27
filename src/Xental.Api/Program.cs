using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Xental.Application;
using Xental.Infrastructure;

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
});

var app = builder.Build();

// Log each HTTP request through Serilog.
app.UseSerilogRequestLogging();

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

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
