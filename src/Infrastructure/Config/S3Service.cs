using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;

namespace RePlace.Infrastructure.Config;

public class S3Service : IS3Service
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    
    public S3Service(IConfiguration configuration)
    {
        var awsOptions = configuration.GetSection("AWS");
        var accessKey = awsOptions["AccessKey"]?.Trim();
        var secretKey = awsOptions["SecretKey"]?.Trim();
        var sessionToken = awsOptions["SessionToken"]?.Trim();
        var region = awsOptions["Region"]?.Trim();
        
        AWSCredentials credentials = !string.IsNullOrEmpty(sessionToken)
            ? new SessionAWSCredentials(accessKey, secretKey, sessionToken)
            : new BasicAWSCredentials(accessKey, secretKey);
        
        _s3Client = new AmazonS3Client(credentials, Amazon.RegionEndpoint.GetBySystemName(region));
        _bucketName = awsOptions["BucketName"]!;
    }
    
    public async Task<PutObjectResponse> UploadFileAsync(Stream fileStream, string fileName, string contentType)
    {
        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = fileName,
            InputStream = fileStream,
            ContentType = contentType
        };

        return await _s3Client.PutObjectAsync(putRequest);
    }
}