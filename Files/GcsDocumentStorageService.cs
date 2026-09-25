// GcsDocumentStorageService.cs
// Talks to Google Cloud Storage for the doctor-to-doctor document-sharing
// feature. Every object this service writes lives under GcsOptions.BaseFolder
// ("DoctorCommunications/SharedDocuments" by default) — a prefix dedicated to
// this feature, separate from any other application's use of the same bucket.
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;

public class GcsDocumentStorageService : IDocumentStorageService
{
    private readonly StorageClient _storageClient;
    private readonly GcsOptions _options;

    public GcsDocumentStorageService(IOptions<GcsOptions> options)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.CredentialsPath))
            throw new InvalidOperationException("Gcs:CredentialsPath is not configured.");
        if (string.IsNullOrWhiteSpace(_options.BucketName))
            throw new InvalidOperationException("Gcs:BucketName is not configured.");

        var credential = CredentialFactory
            .FromFile<ServiceAccountCredential>(_options.CredentialsPath)
            .ToGoogleCredential()
            .CreateScoped("https://www.googleapis.com/auth/devstorage.read_write");

        _storageClient = StorageClient.Create(credential);
    }

    public async Task<string> UploadAsync(string conversationId, string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        var safeName = Path.GetFileName(fileName); // strip any path segments from a client-supplied name
        var objectKey =
            $"{_options.BaseFolder}/{conversationId}/{DateTime.UtcNow:yyyy-MM-dd}/" +
            $"{Guid.NewGuid()}-{safeName}";

        await _storageClient.UploadObjectAsync(_options.BucketName, objectKey, contentType, content, cancellationToken: ct);
        return objectKey;
    }

    public async Task DownloadAsync(string objectKey, Stream destination, CancellationToken ct = default)
    {
        await _storageClient.DownloadObjectAsync(_options.BucketName, objectKey, destination, cancellationToken: ct);
    }
}
