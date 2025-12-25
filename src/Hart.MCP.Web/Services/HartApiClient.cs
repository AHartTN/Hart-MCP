using System.Net.Http.Json;
using System.Text.Json;
using Hart.MCP.Shared.Models;

namespace Hart.MCP.Web.Services;

/// <summary>
/// Client service for communicating with Hart.MCP.Api
/// </summary>
public class HartApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HartApiClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HartApiClient(IHttpClientFactory httpClientFactory, ILogger<HartApiClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient("HartApi");
        _logger = logger;
    }

    /// <summary>
    /// Ingest text into the knowledge substrate
    /// </summary>
    public async Task<ApiResponse<IngestResult>> IngestTextAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/ingest/text",
                new { text },
                JsonOptions,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<IngestResult>(JsonOptions, cancellationToken);
                return ApiResponse<IngestResult>.Ok(result!);
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            return ApiResponse<IngestResult>.Fail($"API error: {response.StatusCode}", error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest text");
            return ApiResponse<IngestResult>.Fail("Request failed", ex.Message);
        }
    }

    /// <summary>
    /// Reconstruct text from a composition atom
    /// </summary>
    public async Task<ApiResponse<string>> ReconstructTextAsync(long atomId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/ingest/text/{atomId}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ReconstructResult>(JsonOptions, cancellationToken);
                return ApiResponse<string>.Ok(result!.Text);
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            return ApiResponse<string>.Fail($"API error: {response.StatusCode}", error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconstruct text for atom {AtomId}", atomId);
            return ApiResponse<string>.Fail("Request failed", ex.Message);
        }
    }

    /// <summary>
    /// Get system statistics
    /// </summary>
    public async Task<ApiResponse<SystemStats>> GetSystemStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("api/ingestionandanalytics/stats", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var wrapper = await response.Content.ReadFromJsonAsync<ApiResponse<SystemStats>>(JsonOptions, cancellationToken);
                return wrapper ?? ApiResponse<SystemStats>.Fail("Invalid response");
            }

            return ApiResponse<SystemStats>.Fail($"API error: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get system stats");
            return ApiResponse<SystemStats>.Fail("Request failed", ex.Message);
        }
    }

    /// <summary>
    /// Health check
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

// DTOs for API responses
public record IngestResult(long AtomId, int CharacterCount, string Status);
public record ReconstructResult(long AtomId, string Text, int CharacterCount, string Status);
