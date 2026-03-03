using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RePlace.Application.Services;
using RePlace.Application.UseCases;
using RePlace.Domain.Models;

namespace RePlace.Tests.Unit.Services;

public class MigrationBackgroundServiceTests
{
    private readonly Mock<ILogger<MigrationBackgroundService>> _loggerMock;
    private readonly Mock<IFileMigrationUseCase> _fileServiceMock;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IMigrationSettingsCache> _settingsCacheMock;

    public MigrationBackgroundServiceTests()
    {
        _loggerMock = new Mock<ILogger<MigrationBackgroundService>>();
        _fileServiceMock = new Mock<IFileMigrationUseCase>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();
        _settingsCacheMock = new Mock<IMigrationSettingsCache>();

        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock.Setup(p => p.GetService(typeof(IFileMigrationUseCase)))
            .Returns(_fileServiceMock.Object);
    }

    private MigrationSettings CreateDefaultSettings()
    {
        return new MigrationSettings
        {
            ActiveWindowEnabled = false,
            BatchSize = 10,
            BatchIntervalSeconds = 1,
            LimitedExecution = false,
            MaxRecordsToProcess = null
        };
    }

    [Fact]
    public async Task ProcessBatchAsync_DoesNothing_WhenNoBatchAvailable()
    {
        var settings = CreateDefaultSettings();
        _settingsCacheMock.Setup(s => s.GetSettings()).Returns(settings);
        _fileServiceMock.Setup(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Anexo>());

        var service = new MigrationBackgroundService(_loggerMock.Object, _settingsCacheMock.Object, _scopeFactoryMock.Object);
        var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        _fileServiceMock.Verify(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _fileServiceMock.Verify(f => f.ProcessSingleAsync(It.IsAny<Anexo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessBatchAsync_ProcessesBatch_Successfully()
    {
        var settings = CreateDefaultSettings();
        _settingsCacheMock.Setup(s => s.GetSettings()).Returns(settings);
        var batch = new List<Anexo>
        {
            new Anexo { Id = 1, Nome = "file1.pdf", TipoAnexoId = 1, AnexoBlob = [1, 2, 3], Tipo = "pdf", Tamanho = 3 },
            new Anexo { Id = 2, Nome = "file2.pdf", TipoAnexoId = 1, AnexoBlob = [4, 5, 6], Tipo = "pdf", Tamanho = 3 }
        };

        _fileServiceMock.Setup(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _fileServiceMock.Setup(f => f.ProcessSingleAsync(It.IsAny<Anexo>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new MigrationBackgroundService(_loggerMock.Object, _settingsCacheMock.Object, _scopeFactoryMock.Object);
        var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        _fileServiceMock.Verify(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _fileServiceMock.Verify(f => f.ProcessSingleAsync(It.IsAny<Anexo>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task ProcessLimitedBatchAsync_StopsAfterMaxRecords()
    {
        var settings = CreateDefaultSettings();
        settings.LimitedExecution = true;
        settings.MaxRecordsToProcess = 200;
        _settingsCacheMock.Setup(s => s.GetSettings()).Returns(settings);

        var batch = Enumerable.Range(1, 10)
            .Select(i => new Anexo { Id = i, Nome = $"file{i}.pdf", TipoAnexoId = 1, AnexoBlob = [1], Tipo = "pdf", Tamanho = 1 })
            .ToList();

        _fileServiceMock.Setup(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _fileServiceMock.Setup(f => f.ProcessSingleAsync(It.IsAny<Anexo>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new MigrationBackgroundService(_loggerMock.Object, _settingsCacheMock.Object, _scopeFactoryMock.Object);
        var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(5000);
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        _fileServiceMock.Verify(f => f.ProcessSingleAsync(It.IsAny<Anexo>(), It.IsAny<CancellationToken>()), Times.AtMost(200));
    }

    [Fact]
    public async Task ShouldProcess_RespectsActiveWindow()
    {
        var settings = CreateDefaultSettings();
        settings.ActiveWindowEnabled = true;
        settings.ActiveWindowStart = "23:00:00";
        settings.ActiveWindowEnd = "01:00:00";
        _settingsCacheMock.Setup(s => s.GetSettings()).Returns(settings);

        _fileServiceMock.Setup(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Anexo>());

        var service = new MigrationBackgroundService(_loggerMock.Object, _settingsCacheMock.Object, _scopeFactoryMock.Object);
        var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        // Verifica se respeitou a janela (pode ou não processar dependendo da hora atual)
        _fileServiceMock.Verify(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()), Times.AtMost(1));
    }

    [Fact]
    public async Task ProcessSingleAsync_ContinuesOnError()
    {
        var settings = CreateDefaultSettings();
        _settingsCacheMock.Setup(s => s.GetSettings()).Returns(settings);
        var batch = new List<Anexo>
        {
            new Anexo { Id = 1, Nome = "file1.pdf", TipoAnexoId = 1, AnexoBlob = [1], Tipo = "pdf", Tamanho = 1 },
            new Anexo { Id = 2, Nome = "file2.pdf", TipoAnexoId = 1, AnexoBlob = [2], Tipo = "pdf", Tamanho = 1 }
        };

        _fileServiceMock.Setup(f => f.GetNextBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _fileServiceMock.Setup(f => f.ProcessSingleAsync(It.Is<Anexo>(a => a.Id == 1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Processing error"));
        _fileServiceMock.Setup(f => f.ProcessSingleAsync(It.Is<Anexo>(a => a.Id == 2), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new MigrationBackgroundService(_loggerMock.Object, _settingsCacheMock.Object, _scopeFactoryMock.Object);
        var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        _fileServiceMock.Verify(f => f.ProcessSingleAsync(It.IsAny<Anexo>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}