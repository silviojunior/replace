using RePlace.Application.UseCases;
using RePlace.Domain.Models;

namespace RePlace.Application.Services;

public class MigrationBackgroundService(
    ILogger<MigrationBackgroundService> logger,
    IMigrationSettingsCache settingsCache,
    IServiceScopeFactory scopeFactory)
    : BackgroundService
{
    private int _totalProcessed;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(":::: REPLACE - INICIANDO PROCESSO DE ETL ::::");
        
        var settings = settingsCache.GetSettings();
        logger.LogInformation(
            "MigrationBackgroundService iniciado. Janela: {Start}-{End}, Batch: {BatchSize}", 
            settings.ActiveWindowStart, settings.ActiveWindowEnd, settings.BatchSize);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                settings = settingsCache.GetSettings();
                
                if (!ShouldProcess(settings))
                {
                    logger.LogDebug("Fora da janela de processamento. Aguardando...");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }
                
                if (settings.LimitedExecution)
                {
                    await ProcessLimitedBatchAsync(settings, stoppingToken);
                }
                else
                {
                    await ProcessBatchAsync(stoppingToken);
                }
                
                await Task.Delay(settings.BatchInterval, stoppingToken);
            }
            catch (Exception ex) when(ex is not OperationCanceledException)
            {
                logger.LogError("Erro no MigrationBackgroundService. Motivo: {ex}", ex);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
    
    private bool ShouldProcess(MigrationSettings settings)
    {
        if (!settings.ActiveWindowEnabled)
            return true;
            
        var now = DateTime.UtcNow.TimeOfDay;
        return now >= settings.ActiveWindowStartTime && 
               now <= settings.ActiveWindowEndTime;
    }

    private async Task ProcessLimitedBatchAsync(MigrationSettings settings, CancellationToken ct)
    {
        var maxRecords = settings.MaxRecordsToProcess ?? int.MaxValue;
        
        logger.LogInformation("[EXTRACT & TRANSFORM] - Iniciando o processamento em batch limitado em ({MaxRecordsToProcess}) registros", maxRecords);
        
        if (_totalProcessed >= maxRecords)
        {
            logger.LogInformation("Limite de ({MaxRecords}) registros atingido. Parando processamento.", maxRecords);
            await StopAsync(ct);
            return;
        }
        
        using var scope = scopeFactory.CreateScope();
        var fileService = scope.ServiceProvider.GetRequiredService<IFileMigrationUseCase>();
        
        var batch = await fileService.GetNextBatchAsync(ct);
        if (batch.Count == 0)
        {
            logger.LogDebug("Nenhum registro pendente para processamento");
            return;
        }
        
        var remaining = maxRecords - _totalProcessed;
        var limitedBatch = batch.Take(remaining).ToList();
        
        logger.LogInformation("Batch reservado: {Count} registros", batch.Count);
        
        var tasks = limitedBatch.Select(anexo => ProcessSingleAsync(anexo, ct));
        var results = await Task.WhenAll(tasks);
        
        var successCount = results.Count(r => r);
        var failCount = results.Count(r => !r);
        
        _totalProcessed += limitedBatch.Count;
        
        logger.LogInformation(
            "Batch processado. Sucessos: {Success}, Falhas: {Fail}. Total processado: {Total}/{Max}", 
            successCount, failCount, _totalProcessed, maxRecords);
        
        if (_totalProcessed >= maxRecords)
        {
            logger.LogInformation(
                "Limite de {MaxRecords} registros atingido. Parando processamento.", 
                maxRecords);
            await StopAsync(ct);
        }
    }
    
    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        logger.LogInformation("[EXTRACT & TRANSFORM] - Iniciando o processamento em batch");
        
        using var scope = scopeFactory.CreateScope();
        var fileService = scope.ServiceProvider.GetRequiredService<IFileMigrationUseCase>();
        
        var batch = await fileService.GetNextBatchAsync(ct);
        
        if (batch.Count == 0)
        {
            logger.LogDebug("Nenhum registro pendente para processamento");
            return;
        }
        
        logger.LogInformation("Batch reservado: {Count} registros", batch.Count);
        
        var tasks = batch.Select(anexo => ProcessSingleAsync(anexo, ct));
        var results = await Task.WhenAll(tasks);
        
        var successCount = results.Count(r => r);
        var failCount = results.Count(r => !r);
        
        logger.LogInformation(
            "Batch processado. Sucessos: {Success}, Falhas: {Fail}", 
            successCount, failCount);
    }
    
    private async Task<bool> ProcessSingleAsync(Anexo anexo, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var fileService = scope.ServiceProvider.GetRequiredService<IFileMigrationUseCase>();
        
        try
        {
            await fileService.ProcessSingleAsync(anexo, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("Erro processando anexo {AnexoId}. Motivo: {ex}", anexo.Id, ex);
            return false;
        }
    }
}
