using Microsoft.EntityFrameworkCore;
using RePlace.Domain.Models;
using RePlace.Infrastructure.Data;

namespace RePlace.Application.Services;

public interface IMigrationSettingsCache
{
    MigrationSettings GetSettings();
}

public class MigrationSettingsCache : IMigrationSettingsCache
{
    private MigrationSettings _cachedSettings;
    private DateTime _lastRefresh;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MigrationSettingsCache> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public MigrationSettingsCache(
        IServiceScopeFactory scopeFactory,
        ILogger<MigrationSettingsCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cachedSettings = LoadSettingsSync();
        _lastRefresh = DateTime.UtcNow;
    }

    public MigrationSettings GetSettings()
    {
        if (DateTime.UtcNow - _lastRefresh > _refreshInterval)
        {
            _ = Task.Run(RefreshSettingsAsync);
        }
        
        return _cachedSettings;
    }

    private async Task RefreshSettingsAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _lastRefresh <= _refreshInterval)
                return;

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await dbContext.MigrationSettings.AsNoTracking().FirstAsync();
            
            _cachedSettings = settings;
            _lastRefresh = DateTime.UtcNow;
            
            _logger.LogInformation("MigrationSettings recarregadas do banco de dados");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao recarregar MigrationSettings. Usando cache anterior.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private MigrationSettings LoadSettingsSync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return dbContext.MigrationSettings.AsNoTracking().First();
    }
}
