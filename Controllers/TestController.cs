using Microsoft.AspNetCore.Mvc;
using Backend.Models;
using Npgsql;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    private readonly string _connectionString;

    public TestController(IConfiguration configuration)
    {
        var rawConnectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? Environment.GetEnvironmentVariable("DATABASE_URL") 
            ?? throw new InvalidOperationException("Database connection string not found");
        
        // Convert PostgreSQL URL to Npgsql connection string format if needed
        _connectionString = ConvertPostgresUrlToConnectionString(rawConnectionString);
    }
    
    private string ConvertPostgresUrlToConnectionString(string connectionString)
    {
        // If it's already a connection string (not a URL), return as-is
        if (!connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }
        
        try
        {
            var uri = new Uri(connectionString);
            var builder = new System.Text.StringBuilder();
            
            // Extract components from URL
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 5432;
            var database = uri.AbsolutePath.TrimStart('/');
            var username = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
            var password = uri.UserInfo.Contains(':') 
                ? Uri.UnescapeDataString(uri.UserInfo.Substring(uri.UserInfo.IndexOf(':') + 1))
                : "";
            
            // Build Npgsql connection string
            builder.Append($"Host={host};Port={port};Database={database};Username={username}");
            if (!string.IsNullOrEmpty(password))
            {
                builder.Append($";Password={password}");
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
            builder.Append($";SSL Mode={sslMode}");
            
            return builder.ToString();
        }
        catch
        {
            // If parsing fails, return original (Npgsql might handle it)
            return connectionString;
        }
    }

    // GET: api/test
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TestProjects>>> GetAll()
    {
        try
    {
        var projects = new List<TestProjects>();
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
            
            // Set search_path to public schema (required because isolated role has restricted search_path)
            // Note: Using string concatenation to avoid $ interpolation issues
            using var setPathCmd = new NpgsqlCommand("SET search_path = public, \"" + "$" + "user\";", conn);
            await setPathCmd.ExecuteNonQueryAsync();
            
        var quote = Convert.ToChar(34).ToString(); // Double quote for PostgreSQL identifier quoting
        var sql = "SELECT " + quote + "Id" + quote + ", " + quote + "Name" + quote + " FROM " + quote + "TestProjects" + quote + " ORDER BY " + quote + "Id" + quote + " ";
        using var cmd = new NpgsqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            projects.Add(new TestProjects
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1)
            });
        }
        return Ok(projects);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // TestProjects table doesn't exist - return empty list gracefully
            // This can happen if the database schema wasn't fully initialized
            return Ok(new List<TestProjects>());
        }
        // Do NOT catch generic Exception - let it bubble up to GlobalExceptionHandlerMiddleware
        // This allows runtime errors to be logged to the error reporting endpoint
    }

    // GET: api/test/5
    [HttpGet("{id}")]
    public async Task<ActionResult<TestProjects>> Get(int id)
    {
        try
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
            
            // Set search_path to public schema (required because isolated role has restricted search_path)
            // Note: Using string concatenation to avoid $ interpolation issues
            using var setPathCmd = new NpgsqlCommand("SET search_path = public, \"" + "$" + "user\";", conn);
            await setPathCmd.ExecuteNonQueryAsync();
            
        var quote = Convert.ToChar(34).ToString(); // Double quote for PostgreSQL identifier quoting
        var sql = "SELECT " + quote + "Id" + quote + ", " + quote + "Name" + quote + " FROM " + quote + "TestProjects" + quote + " WHERE " + quote + "Id" + quote + " = @id ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return Ok(new TestProjects
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1)
            });
        }
        return NotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // TestProjects table doesn't exist - return 404 gracefully
            // This can happen if the database schema wasn't fully initialized
            return NotFound();
        }
        // Do NOT catch generic Exception - let it bubble up to GlobalExceptionHandlerMiddleware
        // This allows runtime errors to be logged to the error reporting endpoint
    }

    // POST: api/test
    [HttpPost]
    public async Task<ActionResult<TestProjects>> Create([FromBody] TestProjects project)
    {
        try
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
            
            // Set search_path to public schema (required because isolated role has restricted search_path)
            // Note: Using string concatenation to avoid $ interpolation issues
            using var setPathCmd = new NpgsqlCommand("SET search_path = public, \"" + "$" + "user\";", conn);
            await setPathCmd.ExecuteNonQueryAsync();
            
        var quote = Convert.ToChar(34).ToString(); // Double quote for PostgreSQL identifier quoting
        var sql = "INSERT INTO " + quote + "TestProjects" + quote + " (" + quote + "Name" + quote + ") VALUES (@name) RETURNING " + quote + "Id" + quote + " ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("name", project.Name);
        var id = await cmd.ExecuteScalarAsync();
        project.Id = Convert.ToInt32(id);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, project);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // TestProjects table doesn't exist - return 503 gracefully
            // This can happen if the database schema wasn't fully initialized
            return StatusCode(503, new { error = "Service Unavailable", message = "Database schema not initialized. Please contact support." });
        }
        // Do NOT catch generic Exception - let it bubble up to GlobalExceptionHandlerMiddleware
        // This allows runtime errors to be logged to the error reporting endpoint
    }

    // PUT: api/test/5
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] TestProjects project)
    {
        try
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
            
            // Set search_path to public schema (required because isolated role has restricted search_path)
            // Note: Using string concatenation to avoid $ interpolation issues
            using var setPathCmd = new NpgsqlCommand("SET search_path = public, \"" + "$" + "user\";", conn);
            await setPathCmd.ExecuteNonQueryAsync();
            
        var quote = Convert.ToChar(34).ToString(); // Double quote for PostgreSQL identifier quoting
        var sql = "UPDATE " + quote + "TestProjects" + quote + " SET " + quote + "Name" + quote + " = @name WHERE " + quote + "Id" + quote + " = @id ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("name", project.Name);
        cmd.Parameters.AddWithValue("id", id);
        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        if (rowsAffected == 0) return NotFound();
        return NoContent();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // TestProjects table doesn't exist - return 404 gracefully
            return NotFound();
        }
        // Do NOT catch generic Exception - let it bubble up to GlobalExceptionHandlerMiddleware
        // This allows runtime errors to be logged to the error reporting endpoint
    }

    // DELETE: api/test/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
            
            // Set search_path to public schema (required because isolated role has restricted search_path)
            // Note: Using string concatenation to avoid $ interpolation issues
            using var setPathCmd = new NpgsqlCommand("SET search_path = public, \"" + "$" + "user\";", conn);
            await setPathCmd.ExecuteNonQueryAsync();
            
        var quote = Convert.ToChar(34).ToString(); // Double quote for PostgreSQL identifier quoting
        var sql = "DELETE FROM " + quote + "TestProjects" + quote + " WHERE " + quote + "Id" + quote + " = @id ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        if (rowsAffected == 0) return NotFound();
        return NoContent();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // TestProjects table doesn't exist - return 404 gracefully
            return NotFound();
        }
        // Do NOT catch generic Exception - let it bubble up to GlobalExceptionHandlerMiddleware
        // This allows runtime errors to be logged to the error reporting endpoint
    }

    // GET: api/test/debug-env
    // Debug endpoint to check environment variables and middleware configuration
    [HttpGet("debug-env")]
    public IActionResult DebugEnv()
    {
        var endpointUrl = Environment.GetEnvironmentVariable("RUNTIME_ERROR_ENDPOINT_URL");
        var allEnvVars = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key.ToString().Contains("RUNTIME") || 
                       e.Key.ToString().Contains("ERROR") || 
                       e.Key.ToString().Contains("DATABASE") ||
                       e.Key.ToString().Contains("PORT"))
            .ToDictionary(e => e.Key.ToString(), e => e.Value?.ToString());
        
        return Ok(new { 
            RUNTIME_ERROR_ENDPOINT_URL = endpointUrl ?? "NOT SET",
            EnvironmentVariables = allEnvVars,
            Message = "Use this endpoint to verify RUNTIME_ERROR_ENDPOINT_URL is set correctly"
        });
    }

    // GET: api/test/test-error/{boardId}
    // Test endpoint that throws an exception to test middleware
    // boardId is included in route so middleware can extract it
    [HttpGet("test-error/{boardId}")]
    public IActionResult TestError(string boardId)
    {
        throw new Exception($"Test exception for middleware debugging (BoardId: {boardId}) - this should be caught by GlobalExceptionHandlerMiddleware");
    }
}
