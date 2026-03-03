using RePlace.Domain.Models;

namespace RePlace.Application.UseCases;

public interface IFileMigrationUseCase
{

    public Task<List<Anexo>> GetNextBatchAsync(CancellationToken ct);
    public Task ProcessSingleAsync(Anexo anexo, CancellationToken ct);
    public (byte[] fileBytes, string checksum) ExtractAndValidate(Anexo anexo);
    public Task<string?> UploadToS3AndValidate(byte[] fileBytes, string fileName, string expectedChecksum, string contentType);
    public Task MarkAsExtracting(int anexoId);
    public Task MarkAsExtracted(int anexoId, string? checksumOrigem);
    public Task MarkAsUploading(int anexoId);
    public Task MarkAsCompleted(int anexoId, string? checksumDestino);
    public Task MarkAsFailed(int anexoId, string failMessage);
}