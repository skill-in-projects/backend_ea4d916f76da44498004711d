using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Backend.Middleware;

public class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Log that middleware is being invoked (using Warning level to ensure it shows up)
        _logger.LogWarning("[MIDDLEWARE] InvokeAsync called for path: {Path}, Method: {Method}", 
            context.Request.Path, context.Request.Method);
        
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // Check if response has already started
            if (context.Response.HasStarted)
            {
                _logger.LogError("[MIDDLEWARE] Response already started - cannot handle exception. Re-throwing.");
                throw; // Re-throw if response started
            }
            
            _logger.LogError(ex, "[MIDDLEWARE] Unhandled exception occurred: {Message}", ex.Message);
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Get the error endpoint URL from environment variable
        var errorEndpointUrl = Environment.GetEnvironmentVariable("RUNTIME_ERROR_ENDPOINT_URL");
        _logger.LogWarning("[MIDDLEWARE] RUNTIME_ERROR_ENDPOINT_URL = {Url}", errorEndpointUrl ?? "NULL");
        
        // If endpoint is configured, send error details to it (fire and forget)
        if (!string.IsNullOrWhiteSpace(errorEndpointUrl))
        {
            _logger.LogWarning("[MIDDLEWARE] Attempting to send error to endpoint: {Url}", errorEndpointUrl);
            
            // CRITICAL: Extract all values from HttpContext BEFORE Task.Run
            // HttpContext will be disposed after this method returns
            var requestPath = context.Request.Path.ToString();
            var requestMethod = context.Request.Method;
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var boardId = ExtractBoardId(context); // Extract BEFORE Task.Run
            
            _logger.LogWarning("[MIDDLEWARE] Extracted values - Path: {Path}, Method: {Method}, BoardId: {BoardId}", 
                requestPath, requestMethod, boardId ?? "NULL");
            
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogWarning("[MIDDLEWARE] Task.Run started, calling SendErrorToEndpointAsync");
                    // Pass extracted values instead of HttpContext
                    await SendErrorToEndpointAsync(errorEndpointUrl, boardId, requestPath, requestMethod, userAgent, exception);
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "[MIDDLEWARE] Failed to send error to endpoint: {Endpoint}", errorEndpointUrl);
                }
            });
        }
        else
        {
            _logger.LogWarning("[MIDDLEWARE] RUNTIME_ERROR_ENDPOINT_URL is not set - skipping error reporting");
        }

        // Return error response to client
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

        var response = new
        {
            error = "An error occurred while processing your request",
            message = exception.Message
        };

        var json = JsonSerializer.Serialize(response);
        await context.Response.WriteAsync(json);
    }

    // Updated method signature - no longer takes HttpContext (which gets disposed)
    private async Task SendErrorToEndpointAsync(
        string endpointUrl, 
        string? boardId, 
        string requestPath, 
        string requestMethod, 
        string userAgent, 
        Exception exception)
    {
        _logger.LogWarning("[MIDDLEWARE] SendErrorToEndpointAsync called with URL: {Url}", endpointUrl);
        
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        _logger.LogWarning("[MIDDLEWARE] Using extracted boardId: {BoardId}", boardId ?? "NULL");

        var errorPayload = new
        {
            boardId = boardId,
            timestamp = DateTime.UtcNow,
            file = GetFileName(exception),
            line = GetLineNumber(exception),
            stackTrace = exception.StackTrace,
            message = exception.Message,
            exceptionType = exception.GetType().Name,
            requestPath = requestPath,  // Use extracted value
            requestMethod = requestMethod,  // Use extracted value
            userAgent = userAgent,  // Use extracted value
            innerException = exception.InnerException != null ? new
            {
                message = exception.InnerException.Message,
                type = exception.InnerException.GetType().Name,
                stackTrace = exception.InnerException.StackTrace
            } : null
        };

        var json = JsonSerializer.Serialize(errorPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogWarning("[MIDDLEWARE] Sending POST request to: {Url}", endpointUrl);
        var response = await httpClient.PostAsync(endpointUrl, content);
        
        _logger.LogWarning("[MIDDLEWARE] Response status: {StatusCode}", response.StatusCode);
        
        if (response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[MIDDLEWARE] Successfully sent runtime error to endpoint: {Endpoint}", endpointUrl);
        }
        else
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("[MIDDLEWARE] Error endpoint returned {StatusCode}: {Response}", response.StatusCode, responseBody);
        }
    }

    private string? ExtractBoardId(HttpContext context)
    {
        // Try route data
        if (context.Request.RouteValues.TryGetValue("boardId", out var boardIdObj))
            return boardIdObj?.ToString();
        
        // Try query string
        if (context.Request.Query.TryGetValue("boardId", out var boardIdQuery))
            return boardIdQuery.ToString();
        
        // Try header
        if (context.Request.Headers.TryGetValue("X-Board-Id", out var boardIdHeader))
            return boardIdHeader.ToString();
        
        // Try environment variable BOARD_ID (set during Railway deployment)
        var boardIdEnv = Environment.GetEnvironmentVariable("BOARD_ID");
        if (!string.IsNullOrWhiteSpace(boardIdEnv))
            return boardIdEnv;
        
        // Try to extract from hostname (Railway pattern: webapi{{boardId}}.up.railway.app - no hyphen)
        var host = context.Request.Host.ToString();
        var hostMatch = System.Text.RegularExpressions.Regex.Match(host, @"webapi([a-f0-9]{{24}})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (hostMatch.Success && hostMatch.Groups.Count > 1)
            return hostMatch.Groups[1].Value;
        
        // Try to extract from RUNTIME_ERROR_ENDPOINT_URL if it contains boardId pattern (no hyphen)
        var endpointUrl = Environment.GetEnvironmentVariable("RUNTIME_ERROR_ENDPOINT_URL");
        if (!string.IsNullOrWhiteSpace(endpointUrl))
        {{
            var urlMatch = System.Text.RegularExpressions.Regex.Match(endpointUrl, @"webapi([a-f0-9]{{24}})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (urlMatch.Success && urlMatch.Groups.Count > 1)
                return urlMatch.Groups[1].Value;
        }}
        
        return null;
    }

    private string? GetFileName(Exception exception)
    {
        var stackTrace = exception.StackTrace;
        if (string.IsNullOrEmpty(stackTrace)) return null;

        // C# stack trace format: "at Namespace.Class.Method() in /path/to/file.cs:line 123"
        // Pattern: "in <path>:line <number>" or "in <path>:<number>"
        var match = System.Text.RegularExpressions.Regex.Match(
            stackTrace,
            @"in\s+([^:]+):(?:line\s+)?(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count > 1)
        {
            var filePath = match.Groups[1].Value.Trim();
            // Extract just the filename from the path
            var lastSlash = filePath.LastIndexOf('/');
            if (lastSlash >= 0)
                return filePath.Substring(lastSlash + 1);
            var lastBackslash = filePath.LastIndexOf('\\');
            if (lastBackslash >= 0)
                return filePath.Substring(lastBackslash + 1);
            return filePath;
        }

        // Fallback: try to get from StackTrace frame if available
        try
        {
            var stackTraceObj = new System.Diagnostics.StackTrace(exception, true);
            if (stackTraceObj.FrameCount > 0)
            {
                var frame = stackTraceObj.GetFrame(0);
                var fileName = frame?.GetFileName();
                if (!string.IsNullOrEmpty(fileName))
                {
                    var lastSlash = fileName.LastIndexOf('/');
                    if (lastSlash >= 0)
                        return fileName.Substring(lastSlash + 1);
                    var lastBackslash = fileName.LastIndexOf('\\');
                    if (lastBackslash >= 0)
                        return fileName.Substring(lastBackslash + 1);
                    return fileName;
                }
            }
        }
        catch
        {
            // Ignore if StackTrace parsing fails
        }

        return null;
    }

    private int? GetLineNumber(Exception exception)
    {
        var stackTrace = exception.StackTrace;
        if (string.IsNullOrEmpty(stackTrace)) return null;

        // C# stack trace format: "at Namespace.Class.Method() in /path/to/file.cs:line 123"
        // Pattern: ":line 123" or ":123"
        var match = System.Text.RegularExpressions.Regex.Match(
            stackTrace,
            @"in\s+[^:]+:(?:line\s+)?(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && match.Groups.Count > 1)
        {
            var lineStr = match.Groups[1].Value;
            if (int.TryParse(lineStr, out var line))
                return line;
        }

        // Fallback: try to get from StackTrace frame if available
        try
        {
            var stackTraceObj = new System.Diagnostics.StackTrace(exception, true);
            if (stackTraceObj.FrameCount > 0)
            {
                var frame = stackTraceObj.GetFrame(0);
                var lineNumber = frame?.GetFileLineNumber();
                if (lineNumber > 0)
                    return lineNumber;
            }
        }
        catch
        {
            // Ignore if StackTrace parsing fails
        }

        return null;
    }
}
