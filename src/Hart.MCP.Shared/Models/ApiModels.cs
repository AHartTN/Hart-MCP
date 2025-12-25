namespace Hart.MCP.Shared.Models;

/// <summary>
/// Generic query result wrapper with timing information
/// </summary>
public class SpatialQueryResult<T>
{
    public List<T> Results { get; set; } = new();
    public int TotalCount { get; set; }
    public double QueryTimeMs { get; set; }
}

/// <summary>
/// Standard API response wrapper
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
    public string[]? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? TraceId { get; set; }

    public static ApiResponse<T> Ok(T data) => new()
    {
        Success = true,
        Data = data
    };

    public static ApiResponse<T> Fail(string error, params string[] details) => new()
    {
        Success = false,
        Error = error,
        Details = details.Length > 0 ? details : null
    };
}

/// <summary>
/// Paginated result wrapper
/// </summary>
public class PaginatedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

/// <summary>
/// Health check response
/// </summary>
public class HealthCheckResponse
{
    public string Status { get; set; } = "Healthy";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Components { get; set; } = new();
}

/// <summary>
/// System statistics
/// </summary>
public class SystemStats
{
    public long TotalAtoms { get; set; }
    public long TotalConstants { get; set; }
    public long TotalCompositions { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public DateTime LastIngestionAt { get; set; }
    public TimeSpan Uptime { get; set; }
}
