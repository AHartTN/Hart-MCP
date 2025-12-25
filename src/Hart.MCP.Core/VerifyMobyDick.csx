// Quick verification script for Moby Dick bit-perfect round-trip
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services;
using Microsoft.EntityFrameworkCore;

var testDataPath = @"D:\Repositories\Hart-MCP\test-data\moby_dick.txt";
if (!File.Exists(testDataPath))
{
    Console.WriteLine("ERROR: moby_dick.txt not found");
    return;
}

// Read original
var original = await File.ReadAllTextAsync(testDataPath);
Console.WriteLine($"Original: {original.Length:N0} characters");
Console.WriteLine($"First 100 chars: {original.Substring(0, 100)}...");
Console.WriteLine($"Last 100 chars: ...{original.Substring(original.Length - 100)}");
Console.WriteLine();

// Setup in-memory DB
var options = new DbContextOptionsBuilder<HartDbContext>()
    .UseInMemoryDatabase($"Verify_{Guid.NewGuid()}")
    .Options;

using var context = new HartDbContext(options);
var atomService = new AtomIngestionService(context);
var ingestionService = new HierarchicalTextIngestionService(context, atomService);
var exportService = new TextExportService(context);

// INGEST
Console.WriteLine("=== INGESTING ===");
var ingestWatch = System.Diagnostics.Stopwatch.StartNew();
var ingestionResult = await ingestionService.IngestTextHierarchicallyAsync(original);
ingestWatch.Stop();
Console.WriteLine($"Ingestion time: {ingestWatch.ElapsedMilliseconds}ms");
Console.WriteLine($"Patterns discovered: {ingestionResult.TotalPatternsDiscovered}");
Console.WriteLine($"Compression ratio: {ingestionResult.CompressionRatio:F2}x");
Console.WriteLine($"Root atom ID: {ingestionResult.RootAtomId}");
Console.WriteLine();

// EXPORT
Console.WriteLine("=== EXPORTING ===");
var exportWatch = System.Diagnostics.Stopwatch.StartNew();
var exportResult = await exportService.ExportTextAsync(ingestionResult.RootAtomId);
exportWatch.Stop();
Console.WriteLine($"Export time: {exportWatch.ElapsedMilliseconds}ms");
Console.WriteLine($"DB queries: {exportResult.Stats.DbQueries}");
Console.WriteLine($"Exported: {exportResult.Text.Length:N0} characters");
Console.WriteLine($"First 100 chars: {exportResult.Text.Substring(0, 100)}...");
Console.WriteLine($"Last 100 chars: ...{exportResult.Text.Substring(exportResult.Text.Length - 100)}");
Console.WriteLine();

// VERIFY
Console.WriteLine("=== VERIFICATION ===");
var match = original == exportResult.Text;
Console.WriteLine($"Length match: {original.Length == exportResult.Text.Length}");
Console.WriteLine($"Content match: {match}");

if (!match)
{
    for (int i = 0; i < Math.Min(original.Length, exportResult.Text.Length); i++)
    {
        if (original[i] != exportResult.Text[i])
        {
            Console.WriteLine($"FIRST DIFFERENCE at index {i}:");
            Console.WriteLine($"  Original: '{original[i]}' (U+{(int)original[i]:X4})");
            Console.WriteLine($"  Exported: '{exportResult.Text[i]}' (U+{(int)exportResult.Text[i]:X4})");
            break;
        }
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════╗");
    Console.WriteLine("║  ✓ BIT-PERFECT VERIFIED                  ║");
    Console.WriteLine($"║  {original.Length:N0} characters matched exactly    ║");
    Console.WriteLine($"║  Round-trip: {ingestWatch.ElapsedMilliseconds + exportWatch.ElapsedMilliseconds}ms total                  ║");
    Console.WriteLine("╚══════════════════════════════════════════╝");
}
