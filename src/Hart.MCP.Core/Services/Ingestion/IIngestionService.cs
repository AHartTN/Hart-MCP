namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Common interface for all ingestion services.
/// Every data type follows the same pattern:
/// 1. Break into atomic units (constants)
/// 2. Project each to hypersphere via seed
/// 3. Build compositions with refs + multiplicities
/// 4. Content-hash for deduplication
/// 5. Return root composition ID
/// </summary>
public interface IIngestionService<T>
{
    /// <summary>
    /// Ingest data into atoms, returning root composition ID
    /// </summary>
    Task<long> IngestAsync(T data, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Reconstruct original data from composition ID
    /// </summary>
    Task<T> ReconstructAsync(long compositionId, CancellationToken cancellationToken = default);
}
