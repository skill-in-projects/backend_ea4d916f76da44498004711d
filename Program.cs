var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS configuration - Allow GitHub Pages and all origins
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        // Allow any origin including GitHub Pages (*.github.io), localhost, and Railway domains
        // Using SetIsOriginAllowed to explicitly allow GitHub Pages and other common origins
        policy.SetIsOriginAllowed(origin =>
        {
            // Allow all origins (GitHub Pages, localhost, Railway, etc.)
            // This is more flexible than AllowAnyOrigin() and allows for future credential support if needed
            if (string.IsNullOrEmpty(origin)) return false;
            
            var uri = new Uri(origin);
            // Allow GitHub Pages (*.github.io)
            if (uri.Host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase))
                return true;
            // Allow localhost (development)
            if (uri.Host == "localhost" || uri.Host == "127.0.0.1")
                return true;
            // Allow Railway domains
            if (uri.Host.EndsWith(".railway.app", StringComparison.OrdinalIgnoreCase))
                return true;
            // Allow all other origins for maximum flexibility
            return true;
        })
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Database connection string from Railway environment variable
// Handle PostgreSQL URLs (e.g., from Neon) by converting to Npgsql connection string format
var rawConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(rawConnectionString))
{
    var connectionString = rawConnectionString;
    
    // If it's a PostgreSQL URL (postgresql://), parse and convert it
    if (connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
        connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var uri = new Uri(connectionString);
            var connStringBuilder = new System.Text.StringBuilder();
            
            // Extract components from URL
            var dbHost = uri.Host;
            var dbPort = uri.Port > 0 ? uri.Port : 5432;
            var database = uri.AbsolutePath.TrimStart('/');
            var username = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
            var password = uri.UserInfo.Contains(':') 
                ? Uri.UnescapeDataString(uri.UserInfo.Substring(uri.UserInfo.IndexOf(':') + 1))
                : "";
            
            // Build Npgsql connection string
            connStringBuilder.Append($"Host={dbHost};Port={dbPort};Database={database};Username={username}");
            if (!string.IsNullOrEmpty(password))
            {
                connStringBuilder.Append($";Password={password}");
            }
            
            // Parse query string for additional parameters (e.g., sslmode)
            var sslMode = "Require";
            if (!string.IsNullOrEmpty(uri.Query) && uri.Query.Length > 1)
            {
                var queryString = uri.Query.Substring(1); // Remove '?'
                var queryParams = queryString.Split('&');
                foreach (var param in queryParams)
                {
                    var parts = param.Split('=');
                    if (parts.Length == 2 && parts[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    {
                        sslMode = Uri.UnescapeDataString(parts[1]);
                        break;
                    }
                }
            }
            connStringBuilder.Append($";SSL Mode={sslMode}");
            
            connectionString = connStringBuilder.ToString();
        }
        catch (Exception ex)
        {
            // If parsing fails, log and use original connection string (Npgsql might handle it)
            Console.WriteLine($"Warning: Failed to parse PostgreSQL URL: {{ex.Message}}");
        }
    }
    
    builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;
}

// Configure URL for Railway deployment
var port = Environment.GetEnvironmentVariable("PORT");
var url = string.IsNullOrEmpty(port) ? "http://0.0.0.0:8080" : $"http://0.0.0.0:{port}";
builder.WebHost.UseUrls(url);

var app = builder.Build();

// Add global exception handler middleware FIRST (before other middleware)
// This ensures it catches all exceptions in the pipeline
app.UseMiddleware<Backend.Middleware.GlobalExceptionHandlerMiddleware>();

// Enable Swagger in all environments (including production)
app.UseSwagger();
app.UseSwaggerUI();

// CORS must be early in the pipeline, before Authorization
app.UseCors("AllowAll");

app.UseAuthorization();
app.MapControllers();

// Add a simple root route to verify the service is running
app.MapGet("/", () => new { 
    message = "Backend API is running", 
    status = "ok",
    swagger = "/swagger",
    api = "/api/test"
});

try
{
app.Run();
}
catch (Exception startupEx)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(startupEx, "[STARTUP ERROR] Application failed to start: {Message}", startupEx.Message);
    
    // Send startup error to endpoint (fire and forget)
    var apiBaseUrl = app.Configuration["ApiBaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiBaseUrl))
    {
        var boardId = Environment.GetEnvironmentVariable("BOARD_ID");
        var endpointUrl = $"{apiBaseUrl.TrimEnd('/')}/api/Mentor/runtime-error";
        
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                
                // Get stack trace line number
                int? lineNumber = null;
                var stackTrace = new System.Diagnostics.StackTrace(startupEx, true);
                var frame = stackTrace.GetFrame(0);
                if (frame?.GetFileLineNumber() > 0)
                {
                    lineNumber = frame.GetFileLineNumber();
                }
                
                var payload = new
                {
                    boardId = boardId,
                    timestamp = DateTime.UtcNow,
                    file = startupEx.Source,
                    line = lineNumber,
                    stackTrace = startupEx.StackTrace,
                    message = startupEx.Message,
                    exceptionType = startupEx.GetType().Name,
                    requestPath = "STARTUP",
                    requestMethod = "STARTUP",
                    userAgent = "STARTUP_ERROR"
                };
                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                await httpClient.PostAsync(endpointUrl, content);
            }
            catch { /* Ignore */ }
        });
    }
    
    throw; // Re-throw to exit with error code
}
