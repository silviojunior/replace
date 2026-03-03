using Microsoft.EntityFrameworkCore;
using RePlace.Application.UseCases;
using RePlace.Domain.Models;
using RePlace.Infrastructure.Data;
using RePlace.Presentation.Dto;

namespace RePlace.Application.Services;

public class MigrationStatusService(AppDbContext context) : IMigrationStatusUseCase
{
    public async Task<MigrationStatusResponseDto> GetDetailedStatusAsync()
    {
        var details = await context.AnexoMigrationStatus
            .GroupBy(s => s.Status)
            .Select(g => new StatusDetailDto
            {
                Status = g.Key.ToString(),
                Count = g.Count(),
                LastUpdated = g.Max(s => s.UpdatedAt)
            })
            .ToListAsync();

        var total = details.Sum(d => d.Count);
        var completed = details.FirstOrDefault(d => d.Status == "Completed")?.Count ?? 0;
        
        var lastFile = await context.AnexoMigrationStatus
            .Where(s => s.Status == StatusEnum.Completed)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => new LastProcessedFileDto
            {
                AnexoId = s.AnexoId,
                NomeAnexo = s.NomeAnexo,
                CompletedAt = s.CompletedAt
            })
            .FirstOrDefaultAsync();

        return new MigrationStatusResponseDto
        {
            Summary = new MigrationStatsDto
            {
                Total = total,
                Completed = completed,
                Failed = details.FirstOrDefault(d => d.Status == "Failed")?.Count ?? 0,
                Pending = details.FirstOrDefault(d => d.Status == "Pending")?.Count ?? 0,
                ProgressPercentage = total > 0 ? Math.Round(completed * 100.0 / total, 2) : 0
            },
            Details = details,
            LastProcessedFile = lastFile
        };
    }

    public async Task<MigrationStatsDto> GetSimpleStatusAsync()
    {
        var stats = await context.AnexoMigrationStatus
            .GroupBy(s => s.Status)
            .ToDictionaryAsync(g => g.Key.ToString(), g => g.Count());

        return new MigrationStatsDto
        {
            Total = stats.Values.Sum(),
            Completed = stats.GetValueOrDefault("Completed", 0),
            Failed = stats.GetValueOrDefault("Failed", 0),
            Pending = stats.GetValueOrDefault("Pending", 0),
            ProgressPercentage = 0
        };
    }
}