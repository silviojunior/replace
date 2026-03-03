using System.Security.Cryptography;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Polly;
using RePlace.Application.UseCases;
using RePlace.Domain.Models;
using RePlace.Infrastructure.Config;
using RePlace.Infrastructure.Data;
using Policy = Polly.Policy;

namespace RePlace.Application.Services;

public class FileMigrationService : IFileMigrationUseCase
{
    private readonly AppDbContext _dbContext;
    private readonly IS3Service _s3Service;
    private readonly IAsyncPolicy _s3RetryPolicy;

    public FileMigrationService(
        AppDbContext dbContext, 
        IS3Service s3Service, 
        ILogger<FileMigrationService> logger)
    {
        _dbContext = dbContext;
        _s3Service = s3Service;
        var log = logger;
        _s3RetryPolicy = Policy
            .Handle<AmazonS3Exception>(ex => 
                ex.ErrorCode == "RequestTimeout" || 
                ex.ErrorCode == "ServiceUnavailable" ||
                ex.ErrorCode == "SlowDown" ||
                (int)ex.StatusCode >= 500) 
            .Or<HttpRequestException>() 
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => 
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), 
                onRetry: (exception, delay, retryCount) =>
                {
                    log.LogInformation("Retry {retryCount} após {delay} devido a: {exceptionMessage}", retryCount, delay, exception.Message);
                });
    }

    public async Task<List<Anexo>> GetNextBatchAsync(CancellationToken ct)
    {
        var settings = await _dbContext.MigrationSettings.FirstAsync(ct);
        var podId = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;
        var lockExpires = DateTime.UtcNow.Add(settings.LockTimeout);
        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            var query = $"""
                                 SELECT a.* 
                                 FROM anexo a
                                 LEFT JOIN anexo_migration_status s ON a.id = s.anexo_id
                                 WHERE s.id IS NULL 
                                    OR (s.status = 'Failed' AND s.retry_count < 3)
                                 ORDER BY a.id ASC
                                 LIMIT {settings.BatchSize}
                                 FOR UPDATE SKIP LOCKED
                         """;

            var anexos = await _dbContext.Anexos
                .FromSqlRaw(query)
                .AsNoTracking()
                .ToListAsync(ct);

            if (anexos.Count == 0)
                return [];

            foreach (var anexo in anexos)
                await UpdateStatusForBatchAsync(anexo, podId, lockExpires, ct);

            await _dbContext.SaveChangesAsync(ct);

            return anexos;
        });
    }

    private async Task UpdateStatusForBatchAsync(
        Anexo anexo, 
        string podId, 
        DateTime lockExpires, 
        CancellationToken ct)
    {
        var status = await _dbContext.AnexoMigrationStatus
            .FirstOrDefaultAsync(s => s.AnexoId == anexo.Id, ct);

        if (status == null)
        {
            status = new AnexoMigrationStatus
            {
                AnexoId = anexo.Id,
                NomeAnexo = anexo.Nome,
                Status = StatusEnum.Pending,
                RetryCount = 0,
                ProcessingPodId = podId,
                ProcessingStartedAt = DateTime.UtcNow,
                LockExpiresAt = lockExpires,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.AnexoMigrationStatus.Add(status);
        }
        else
        {
            status.Status = StatusEnum.Pending;
            status.ProcessingPodId = podId;
            status.ProcessingStartedAt = DateTime.UtcNow;
            status.LockExpiresAt = lockExpires;
            status.RetryCount++;
            _dbContext.AnexoMigrationStatus.Update(status);
        }
    }
    
    public async Task ProcessSingleAsync(Anexo anexo, CancellationToken ct)
    {
        var settings = await _dbContext.MigrationSettings.FirstAsync(ct);
        var logger = _dbContext.GetService<ILogger<MigrationBackgroundService>>();
        var fileName = $"{GetFolderByType(anexo.TipoAnexoId)}/{anexo.Id}_{anexo.Nome}";
        
        try
        {
            await MarkAsExtracting(anexo.Id);
        
            var (fileBytes, checksum) = ExtractAndValidate(anexo);
            await MarkAsExtracted(anexo.Id, checksum);
        
            await MarkAsUploading(anexo.Id);
        
            var etag = await UploadToS3AndValidate(
                fileBytes, 
                fileName, 
                checksum, 
                anexo.Tipo);

            await SaveFilepath(anexo.Id, fileName);
            
            if (settings.PurgeFiles)
                await RemoveFileFromDatabase(anexo.Id);
            
            await MarkAsCompleted(anexo.Id, etag);
        }
        catch (Exception ex)
        {
            logger.LogError("[ERROR] - Erro ao migrar o arquivo ({anexoId}). Motivo: {ex}", anexo.Id, ex);
            await MarkAsFailed(anexo.Id, ex.Message);
            throw;
        }
    }

    private async Task SaveFilepath(int anexoId, string fileName)
    {
        var anexo = await _dbContext.Anexos.FindAsync(anexoId);
        if (anexo != null)
        {
            anexo.Filepath = fileName;
            await _dbContext.SaveChangesAsync();
        }
    }
    
    private async Task RemoveFileFromDatabase(int anexoId)
    {
        var anexo = await _dbContext.Anexos.FindAsync(anexoId);
        if (anexo != null)
        {
            anexo.AnexoBlob = null;
            await _dbContext.SaveChangesAsync();
        }
    }
    
    private string GetFolderByType(int attachmentType)
    {
        var folderName = attachmentType switch
        {
            1 => "imagem",
            2 => "planta_laboratorio_aprovado",
            3 => "documento",
            4 => "planta_laboratorio_ressalvas",
            5 => "termo_de_autorizacao_de_laboratorio",
            _ => "outros"
        };

        return folderName;
    }

    public (byte[] fileBytes, string checksum) ExtractAndValidate(Anexo anexo)
    {
        if (anexo.AnexoBlob == null || anexo.AnexoBlob.Length == 0)
            throw new ArgumentException("Conteúdo do anexo vazio ou nulo");

        using var md5 = MD5.Create();
        var hashBytes = MD5.HashData(anexo.AnexoBlob);
        var checksum = Convert.ToHexStringLower(hashBytes);

        return string.IsNullOrEmpty(checksum)
            ? throw new InvalidOperationException("Falha ao calcular checksum")
            : (Anexo: anexo.AnexoBlob, checksum);
    }

    public async Task<string?> UploadToS3AndValidate(
        byte[] fileBytes, 
        string fileName, 
        string expectedChecksum,
        string contentType)
    {
        return await _s3RetryPolicy.ExecuteAsync(async () =>
        {
            using var stream = new MemoryStream(fileBytes);
            var response = await _s3Service.UploadFileAsync(stream, fileName, contentType);
            var etag = response.ETag?.Trim('"');

            return etag != expectedChecksum
                ? throw new InvalidOperationException(
                    $"Incompatibilidade em soma de verificação (Checksum). S3: {etag}, Local: {expectedChecksum}")
                : etag;
        });
    }

    public async Task MarkAsExtracting(int anexoId)
    {
        var status = await GetStatusAsync(anexoId);
        await ChangeStatus(StatusEnum.Extracting, status);
    }
    
    public async Task MarkAsExtracted(int anexoId, string? checksumOrigem)
    {
        var status = await GetStatusAsync(anexoId);
        status.ChecksumOrigem = checksumOrigem ?? string.Empty;
        await ChangeStatus(StatusEnum.Extracted, status);
    }
    
    public async Task MarkAsUploading(int anexoId)
    {
        var status = await GetStatusAsync(anexoId);
        await ChangeStatus(StatusEnum.Uploading, status);
    }
    
    public async Task MarkAsCompleted(int anexoId, string? checksumDestino)
    {
        var status = await GetStatusAsync(anexoId);

        status.ChecksumDestino = checksumDestino ?? string.Empty;
        status.CompletedAt = DateTime.UtcNow;
    
        await ChangeStatus(StatusEnum.Completed, status);
    }
    
    public async Task MarkAsFailed(int anexoId, string failMessage)
    {
        var status = await GetStatusAsync(anexoId);

        status.ErrorMessage = failMessage;
        status.RetryCount++;
        
        await ChangeStatus(StatusEnum.Failed, status);
    }
    
    private async Task<AnexoMigrationStatus> GetStatusAsync(int anexoId)
    {
        var status = await _dbContext.AnexoMigrationStatus
            .FirstOrDefaultAsync(s => s.AnexoId == anexoId);

        return status ?? throw new ArgumentException($"Status não encontrado para o anexo {anexoId}");
    }
    
    private async Task ChangeStatus(StatusEnum status, AnexoMigrationStatus anexoMigrationStatus)
    {
        anexoMigrationStatus.Status = status;
        anexoMigrationStatus.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }
}
