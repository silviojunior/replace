using Amazon.S3.Model;

namespace RePlace.Infrastructure.Config;

public interface IS3Service
{
    Task<PutObjectResponse> UploadFileAsync(Stream fileStream, string fileName, string contentType);
}