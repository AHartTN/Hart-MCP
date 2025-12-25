// Quick script to seed full Unicode into database
// Run with: dotnet run SeedDatabase.cs

using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services.Ingestion;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

var connectionStringNpgsql = "Host=localhost;Port=5432;Database=HART-MCP;Username=hartonomous;Password=hartonomous";
var connectionStringLibpq = "host=localhost port=5432 dbname=HART-MCP user=hartonomous password=hartonomous";

Console.WriteLine("=== HART-MCP DATABASE SEEDING ===");
Console.WriteLine($"Target: 1,114,112 Unicode codepoints");
Console.WriteLine("");

var sw = Stopwatch.StartNew();

using var service = new NativeBulkIngestionService(connectionStringLibpq);
var progress = new Progress<UnicodeSeedProgress>(p =>
{
    if (p.CodepointsSeeded % 100000 == 0 || p.CodepointsSeeded == p.TotalCodepoints)
    {
        Console.WriteLine($"  [{sw.ElapsedMilliseconds,6}ms] {p.CodepointsSeeded:N0}/{p.TotalCodepoints:N0} ({p.PercentComplete:F1}%)");
    }
});

var count = await service.SeedUnicodeAsync(fullUnicode: true, progress: progress);
sw.Stop();

Console.WriteLine("");
Console.WriteLine($"✓ Seeded {count:N0} codepoints in {sw.ElapsedMilliseconds:N0}ms");
Console.WriteLine($"  Rate: {count * 1000.0 / sw.ElapsedMilliseconds:N0} codepoints/sec");
