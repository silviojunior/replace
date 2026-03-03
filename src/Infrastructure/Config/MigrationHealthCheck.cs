using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RePlace.Domain.Models;
using RePlace.Infrastructure.Data;

namespace RePlace.Infrastructure.Config;

public class MigrationHealthCheck(AppDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context1, 
        CancellationToken ct = default)
    {
        try
        {
            var stats = await context.AnexoMigrationStatus
                .GroupBy(s => s.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            
            var data = new Dictionary<string, object>
            {
                ["total"] = stats.Sum(s => s.Count),
                ["completed"] = stats.FirstOrDefault(s => s.Status == StatusEnum.Completed)?.Count ?? 0,
                ["failed"] = stats.FirstOrDefault(s => s.Status == StatusEnum.Failed)?.Count ?? 0,
                ["pending"] = stats.FirstOrDefault(s => s.Status == StatusEnum.Pending)?.Count ?? 0
            };
            
            return HealthCheckResult.Healthy("Migration service operational", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Migration service unhealthy", ex);
        }
    }
}