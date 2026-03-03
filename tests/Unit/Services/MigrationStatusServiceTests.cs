using Microsoft.EntityFrameworkCore;
using RePlace.Application.Services;
using RePlace.Domain.Models;
using RePlace.Infrastructure.Data;

namespace RePlace.Tests.Unit.Services;

public class MigrationStatusServiceTests
{
    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async Task GetDetailedStatusAsync_ReturnsEmptyStats_WhenNoData()
    {
        using var context = CreateContext();
        var service = new MigrationStatusService(context);

        var result = await service.GetDetailedStatusAsync();

        Assert.NotNull(result);
        Assert.Equal(0, result.Summary.Total);
        Assert.Equal(0, result.Summary.Completed);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Equal(0, result.Summary.Pending);
        Assert.Equal(0, result.Summary.ProgressPercentage);
        Assert.Empty(result.Details);
        Assert.Null(result.LastProcessedFile);
    }

    [Fact]
    public async Task GetDetailedStatusAsync_ReturnsCorrectStats_WithData()
    {
        using var context = CreateContext();
        var service = new MigrationStatusService(context);

        context.AnexoMigrationStatus.AddRange(
            new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "file1.pdf", Status = StatusEnum.Completed, CompletedAt = DateTime.UtcNow },
            new AnexoMigrationStatus { AnexoId = 2, NomeAnexo = "file2.pdf", Status = StatusEnum.Completed, CompletedAt = DateTime.UtcNow.AddMinutes(-5) },
            new AnexoMigrationStatus { AnexoId = 3, NomeAnexo = "file3.pdf", Status = StatusEnum.Failed, RetryCount = 1 },
            new AnexoMigrationStatus { AnexoId = 4, NomeAnexo = "file4.pdf", Status = StatusEnum.Pending }
        );
        await context.SaveChangesAsync();

        var result = await service.GetDetailedStatusAsync();

        Assert.Equal(4, result.Summary.Total);
        Assert.Equal(2, result.Summary.Completed);
        Assert.Equal(1, result.Summary.Failed);
        Assert.Equal(1, result.Summary.Pending);
        Assert.Equal(50, result.Summary.ProgressPercentage);
        Assert.Equal(3, result.Details.Count);
        Assert.NotNull(result.LastProcessedFile);
        Assert.Equal(1, result.LastProcessedFile.AnexoId);
    }

    [Fact]
    public async Task GetDetailedStatusAsync_CalculatesProgressCorrectly()
    {
        using var context = CreateContext();
        var service = new MigrationStatusService(context);

        context.AnexoMigrationStatus.AddRange(
            new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "file1.pdf", Status = StatusEnum.Completed, CompletedAt = DateTime.UtcNow },
            new AnexoMigrationStatus { AnexoId = 2, NomeAnexo = "file2.pdf", Status = StatusEnum.Pending },
            new AnexoMigrationStatus { AnexoId = 3, NomeAnexo = "file3.pdf", Status = StatusEnum.Pending }
        );
        await context.SaveChangesAsync();

        var result = await service.GetDetailedStatusAsync();

        Assert.Equal(3, result.Summary.Total);
        Assert.Equal(1, result.Summary.Completed);
        Assert.Equal(33.33, result.Summary.ProgressPercentage);
    }

    [Fact]
    public async Task GetDetailedStatusAsync_ReturnsLastProcessedFile()
    {
        using var context = CreateContext();
        var service = new MigrationStatusService(context);

        var now = DateTime.UtcNow;
        context.AnexoMigrationStatus.AddRange(
            new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "old.pdf", Status = StatusEnum.Completed, CompletedAt = now.AddHours(-2) },
            new AnexoMigrationStatus { AnexoId = 2, NomeAnexo = "recent.pdf", Status = StatusEnum.Completed, CompletedAt = now },
            new AnexoMigrationStatus { AnexoId = 3, NomeAnexo = "pending.pdf", Status = StatusEnum.Pending }
        );
        await context.SaveChangesAsync();

        var result = await service.GetDetailedStatusAsync();

        Assert.NotNull(result.LastProcessedFile);
        Assert.Equal(2, result.LastProcessedFile.AnexoId);
        Assert.Equal("recent.pdf", result.LastProcessedFile.NomeAnexo);
    }

    [Fact]
    public async Task GetSimpleStatusAsync_ReturnsEmptyStats_WhenNoData()
    {
        using var context = CreateContext();
        var service = new MigrationStatusService(context);

        var result = await service.GetSimpleStatusAsync();

        Assert.NotNull(result);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Pending);
    }

    [Fact]
    public async Task GetSimpleStatusAsync_ReturnsCorrectStats_WithData()
    {
        using var context = CreateContext();
        var service = new MigrationStatusService(context);

        context.AnexoMigrationStatus.AddRange(
            new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "file1.pdf", Status = StatusEnum.Completed, CompletedAt = DateTime.UtcNow },
            new AnexoMigrationStatus { AnexoId = 2, NomeAnexo = "file2.pdf", Status = StatusEnum.Completed, CompletedAt = DateTime.UtcNow },
            new AnexoMigrationStatus { AnexoId = 3, NomeAnexo = "file3.pdf", Status = StatusEnum.Failed, RetryCount = 1 },
            new AnexoMigrationStatus { AnexoId = 4, NomeAnexo = "file4.pdf", Status = StatusEnum.Pending },
            new AnexoMigrationStatus { AnexoId = 5, NomeAnexo = "file5.pdf", Status = StatusEnum.Pending }
        );
        await context.SaveChangesAsync();

        var result = await service.GetSimpleStatusAsync();

        Assert.Equal(5, result.Total);
        Assert.Equal(2, result.Completed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Pending);
    }
}
