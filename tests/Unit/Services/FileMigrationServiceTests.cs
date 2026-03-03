using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RePlace.Application.Services;
using RePlace.Domain.Models;
using RePlace.Infrastructure.Config;
using RePlace.Infrastructure.Data;

namespace RePlace.Tests.Unit.Services;

public class FileMigrationServiceTests
{
    private readonly Mock<IS3Service> _s3ServiceMock = new();
    private readonly Mock<ILogger<FileMigrationService>> _loggerMock = new();
    private readonly Mock<ILogger<MigrationBackgroundService>> _backgroundLoggerMock = new();

    private AppDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void ExtractAndValidate_ThrowsException_WhenAnexoIsNull()
    {
        using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var anexo = new Anexo { Nome = "test.pdf", TipoAnexoId = 1, AnexoBlob = null!, Tipo = "pdf", Tamanho = 0 };
        
        Assert.Throws<ArgumentException>(() => service.ExtractAndValidate(anexo));
    }

    [Fact]
    public void ExtractAndValidate_ThrowsException_WhenAnexoIsEmpty()
    {
        using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var anexo = new Anexo { Nome = "test.pdf", TipoAnexoId = 1, AnexoBlob = [], Tipo = "pdf", Tamanho = 0 };
        
        Assert.Throws<ArgumentException>(() => service.ExtractAndValidate(anexo));
    }

    [Fact]
    public void ExtractAndValidate_ReturnsFileAndChecksum_WhenValid()
    {
        using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var fileBytes = "test content"u8.ToArray();
        var anexo = new Anexo { Nome = "test.pdf", TipoAnexoId = 1, AnexoBlob = fileBytes, Tipo = "pdf", Tamanho = fileBytes.Length };

        var (bytes, checksum) = service.ExtractAndValidate(anexo);

        Assert.Equal(fileBytes, bytes);
        Assert.NotEmpty(checksum);
        Assert.Equal(32, checksum.Length);
    }

    [Fact]
    public async Task UploadToS3AndValidate_ReturnsETag_WhenChecksumMatches()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var fileBytes = "test"u8.ToArray();
        const string checksum = "098f6bcd4621d373cade4e832627b4f6";
        _s3ServiceMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), "application/pdf"))
            .ReturnsAsync(new PutObjectResponse { ETag = $"\"{checksum}\"" });

        var result = await service.UploadToS3AndValidate(fileBytes, "test.pdf", checksum, "application/pdf");

        Assert.Equal(checksum, result);
    }

    [Fact]
    public async Task UploadToS3AndValidate_ThrowsException_WhenChecksumMismatch()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var fileBytes = "test"u8.ToArray();
        _s3ServiceMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), "application/pdf"))
            .ReturnsAsync(new PutObjectResponse { ETag = "\"wrongchecksum\"" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            service.UploadToS3AndValidate(fileBytes, "test.pdf", "correctchecksum", "application/pdf"));
    }

    [Fact]
    public async Task ProcessSingleAsync_ProcessesFileSuccessfully()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddEntityFrameworkInMemoryDatabase();
        serviceCollection.AddSingleton(_backgroundLoggerMock.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseInternalServiceProvider(serviceProvider)
            .Options;
        
        await using var context = new AppDbContext(options);
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var anexo = new Anexo { Id = 1, Nome = "test.pdf", TipoAnexoId = 1, AnexoBlob = "content"u8.ToArray(), Tipo = "pdf", Tamanho = 7, Filepath = string.Empty };
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Pending };
        var settings = new MigrationSettings();
        context.Anexos.Add(anexo);
        context.AnexoMigrationStatus.Add(status);
        context.MigrationSettings.Add(settings);
        await context.SaveChangesAsync();

        _s3ServiceMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new PutObjectResponse { ETag = "\"9a0364b9e99bb480dd25e1f0284c8555\"" });

        await service.ProcessSingleAsync(anexo, CancellationToken.None);

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Completed, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        
        serviceProvider.Dispose();
    }

    [Fact]
    public async Task ProcessSingleAsync_MarksAsFailed_OnException()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddEntityFrameworkInMemoryDatabase();
        serviceCollection.AddSingleton(_backgroundLoggerMock.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseInternalServiceProvider(serviceProvider)
            .Options;
        
        await using var context = new AppDbContext(options);
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var anexo = new Anexo { Id = 1, Nome = "test.pdf", TipoAnexoId = 1, AnexoBlob = "content"u8.ToArray(), Tipo = "pdf", Tamanho = 7, Filepath = string.Empty };
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Pending };
        var settings = new MigrationSettings();
        context.Anexos.Add(anexo);
        context.AnexoMigrationStatus.Add(status);
        context.MigrationSettings.Add(settings);
        await context.SaveChangesAsync();

        _s3ServiceMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("S3 error"));

        await Assert.ThrowsAsync<Exception>(() => service.ProcessSingleAsync(anexo, CancellationToken.None));

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Failed, updated.Status);
        Assert.Contains("S3 error", updated.ErrorMessage);
        
        serviceProvider.Dispose();
    }

    [Fact]
    public async Task MarkAsExtracting_UpdatesStatusToExtracting()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Pending };
        context.AnexoMigrationStatus.Add(status);
        await context.SaveChangesAsync();

        await service.MarkAsExtracting(1);

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Extracting, updated.Status);
    }

    [Fact]
    public async Task MarkAsExtracted_UpdatesStatusAndChecksum()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Extracting };
        context.AnexoMigrationStatus.Add(status);
        await context.SaveChangesAsync();

        await service.MarkAsExtracted(1, "abc123");

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Extracted, updated.Status);
        Assert.Equal("abc123", updated.ChecksumOrigem);
    }

    [Fact]
    public async Task MarkAsUploading_UpdatesStatusToUploading()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Extracted };
        context.AnexoMigrationStatus.Add(status);
        await context.SaveChangesAsync();

        await service.MarkAsUploading(1);

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Uploading, updated.Status);
    }

    [Fact]
    public async Task MarkAsCompleted_UpdatesStatusChecksumAndCompletedAt()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Uploading };
        context.AnexoMigrationStatus.Add(status);
        await context.SaveChangesAsync();

        await service.MarkAsCompleted(1, "xyz789");

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Completed, updated.Status);
        Assert.Equal("xyz789", updated.ChecksumDestino);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task MarkAsFailed_UpdatesStatusErrorMessageAndRetryCount()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Uploading, RetryCount = 0 };
        context.AnexoMigrationStatus.Add(status);
        await context.SaveChangesAsync();

        await service.MarkAsFailed(1, "Upload failed");

        var updated = await context.AnexoMigrationStatus.FirstAsync(s => s.AnexoId == 1);
        Assert.Equal(StatusEnum.Failed, updated.Status);
        Assert.Equal("Upload failed", updated.ErrorMessage);
        Assert.Equal(1, updated.RetryCount);
    }

    [Fact]
    public async Task MarkAsExtracting_ThrowsException_WhenStatusNotFound()
    {
        await using var context = CreateInMemoryContext();
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        
        await Assert.ThrowsAsync<ArgumentException>(() => service.MarkAsExtracting(999));
    }

    [Fact]
    public async Task ProcessSingleAsync_SavesFilepath_WhenProcessingSucceeds()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddEntityFrameworkInMemoryDatabase();
        serviceCollection.AddSingleton(_backgroundLoggerMock.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseInternalServiceProvider(serviceProvider)
            .Options;
        
        await using var context = new AppDbContext(options);
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var anexo = new Anexo { Id = 1, Nome = "test.pdf", TipoAnexoId = 3, AnexoBlob = "content"u8.ToArray(), Tipo = "pdf", Tamanho = 7, Filepath = string.Empty };
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Pending };
        var settings = new MigrationSettings();
        context.Anexos.Add(anexo);
        context.AnexoMigrationStatus.Add(status);
        context.MigrationSettings.Add(settings);
        await context.SaveChangesAsync();

        _s3ServiceMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new PutObjectResponse { ETag = "\"9a0364b9e99bb480dd25e1f0284c8555\"" });

        await service.ProcessSingleAsync(anexo, CancellationToken.None);

        var updated = await context.Anexos.FirstAsync(a => a.Id == 1);
        Assert.NotNull(updated.Filepath);
        Assert.Equal("documento/1_test.pdf", updated.Filepath);
        
        serviceProvider.Dispose();
    }

    [Fact]
    public async Task ProcessSingleAsync_RemovesFileFromDatabase_WhenPurgeFilesIsTrue()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddEntityFrameworkInMemoryDatabase();
        serviceCollection.AddSingleton(_backgroundLoggerMock.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseInternalServiceProvider(serviceProvider)
            .Options;
        
        await using var context = new AppDbContext(options);
        var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
        var fileContent = "content"u8.ToArray();
        var anexo = new Anexo { Id = 1, Nome = "test.pdf", TipoAnexoId = 1, AnexoBlob = fileContent, Tipo = "pdf", Tamanho = 7, Filepath = string.Empty };
        var status = new AnexoMigrationStatus { AnexoId = 1, NomeAnexo = "test.pdf", Status = StatusEnum.Pending };
        var settings = new MigrationSettings { PurgeFiles = true };
        context.Anexos.Add(anexo);
        context.AnexoMigrationStatus.Add(status);
        context.MigrationSettings.Add(settings);
        await context.SaveChangesAsync();

        _s3ServiceMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new PutObjectResponse { ETag = "\"9a0364b9e99bb480dd25e1f0284c8555\"" });

        await service.ProcessSingleAsync(anexo, CancellationToken.None);

        var updated = await context.Anexos.FirstAsync(a => a.Id == 1);
        Assert.Null(updated.AnexoBlob);
        
        serviceProvider.Dispose();
    }
}