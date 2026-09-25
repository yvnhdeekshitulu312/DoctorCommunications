// IDocumentStorageService.cs
public interface IDocumentStorageService
{
    /// <summary>
    /// Uploads a shared document under this feature's dedicated GCS prefix and
    /// returns the object key it was stored at (not a public URL).
    /// </summary>
    Task<string> UploadAsync(string conversationId, string fileName, string contentType, Stream content, CancellationToken ct = default);

    /// <summary>Streams a previously-uploaded object into <paramref name="destination"/>.</summary>
    Task DownloadAsync(string objectKey, Stream destination, CancellationToken ct = default);
}
