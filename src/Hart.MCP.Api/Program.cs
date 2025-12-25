using Hart.MCP.Api.Middleware;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services;
using Hart.MCP.Core.Services.Ingestion;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// Register exception handler
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Rate limiting configuration
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    // Global limiter: 100 requests per minute per IP
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));
    
    // Named policy for ingestion: more restrictive (10 req/min)
    options.AddPolicy("ingestion", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));
    
    // Named policy for queries: less restrictive (200 req/min)
    options.AddPolicy("query", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            }));
});

// Configure connection string with proper pooling
var connectionString = builder.Configuration.GetConnectionString("HartMCP") 
    ?? "Host=localhost;Port=5432;Database=HART-MCP;Username=hartonomous;Password=hartonomous";

// Ensure connection string has proper pooling settings
if (!connectionString.Contains("Pooling=", StringComparison.OrdinalIgnoreCase))
{
    connectionString += ";Pooling=true;Minimum Pool Size=5;Maximum Pool Size=100;Connection Idle Lifetime=300";
}

// Configure EF Core with PostGIS - Single unified HartDbContext for ALL data
// Everything is an Atom with AtomType discrimination and JSONB metadata
// Use DbContextFactory for high-performance parallel scenarios
builder.Services.AddDbContextFactory<HartDbContext>(options =>
{
    options.UseNpgsql(connectionString, o => 
    {
        o.UseNetTopologySuite();
        o.MigrationsAssembly("Hart.MCP.Api");
        o.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
        o.CommandTimeout(30);
        o.MinBatchSize(2);
        o.MaxBatchSize(100);
    });
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// Also register scoped DbContext for simple use cases
builder.Services.AddScoped<HartDbContext>(sp => 
    sp.GetRequiredService<IDbContextFactory<HartDbContext>>().CreateDbContext());

// Register services - both standard and optimized versions
builder.Services.AddScoped<AtomIngestionService>();
builder.Services.AddScoped<SpatialQueryService>();
builder.Services.AddScoped<AIQueryService>();

// High-performance parallel services (use IDbContextFactory for thread safety)
builder.Services.AddSingleton<Hart.MCP.Core.Services.Optimized.ParallelIngestionService>();
builder.Services.AddSingleton<Hart.MCP.Core.Services.Optimized.ParallelSpatialQueryService>();

// CORS configuration
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        }
        else
        {
            // Production: restrict origins
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
                ?? ["https://localhost"];
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// Add problem details for consistent error responses
builder.Services.AddProblemDetails();

// Add response caching
builder.Services.AddResponseCaching();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
    app.UseHsts();
}

// Add request timing middleware early in pipeline
app.UseRequestTiming();

app.UseHttpsRedirection();
app.UseResponseCompression();
app.UseResponseCaching();
app.UseCors();
app.UseRateLimiter();

// Health check endpoint
app.MapHealthChecks("/health");

app.MapControllers();

app.Run();
