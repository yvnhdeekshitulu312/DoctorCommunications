// DocumentEndpoints.cs
// Minimal-API endpoints for doctor-to-doctor document sharing inside a
// conversation. Every uploaded object lives under GcsOptions.BaseFolder in
// GCS; only accepted conversation participants can upload, list or download.
// Same trust model as the rest of this API: *UserId is caller-asserted, no
// auth middleware here yet, but every call is scoped to accepted participants.
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

public static class DocumentEndpoints
{
    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/gif",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/plain"
    };

    public static void MapDocumentEndpoints(this WebApplication app)
    {
        // ── Upload a document into a conversation ──────────────────────────
        app.MapPost("/api/conversations/{conversationId}/documents", async (
            string conversationId,
            IFormFile file,
            [FromForm] string uploaderUserId,
            [FromForm] string uploaderName,
            DoctorCommunicationsDal dal,
            IDocumentStorageService storage,
            IHubContext<CallHub> hub,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest("No file uploaded.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest($"File exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB limit.");
            if (string.IsNullOrWhiteSpace(uploaderUserId))
                return Results.BadRequest("uploaderUserId is required.");

            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
            if (!AllowedContentTypes.Contains(contentType))
                return Results.BadRequest($"File type '{contentType}' is not allowed.");

            string objectKey;
            await using (var stream = file.OpenReadStream())
            {
                objectKey = await storage.UploadAsync(conversationId, file.FileName, contentType, stream, ct);
            }

            var saved = await dal.SaveSharedDocumentAsync(
                conversationId, uploaderUserId, uploaderName, file.FileName, contentType, file.Length, objectKey, ct);

            if (saved is null)
            {
                // Uploader wasn't an accepted participant. The object is already
                // in GCS at this point (write-then-check, same order as the old
                // FileController) but nothing references it — orphaned rather
                // than silently accessible to a non-participant.
                logger.LogWarning(
                    "UploadDocument: uploaderUserId={UploaderUserId} is not an accepted participant of conversationId={ConversationId} — object {ObjectKey} was written to GCS but not linked",
                    uploaderUserId, conversationId, objectKey);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await hub.Clients.Group($"chat-{conversationId}").SendAsync("DocumentShared", new
            {
                id = saved.Id,
                conversationId = saved.ConversationId,
                uploaderUserId = saved.UploaderUserId,
                uploaderName = saved.UploaderName,
                fileName = saved.FileName,
                contentType = saved.ContentType,
                sizeBytes = saved.SizeBytes,
                uploadedAtUtc = saved.UploadedAtUtc
            }, cancellationToken: ct);

            return Results.Ok(saved);
        })
        // .NET 8+ auto-attaches antiforgery metadata to any endpoint with an
        // IFormFile parameter, regardless of whether antiforgery middleware is
        // registered — and then throws at request time when it isn't (this app
        // has no AddAntiforgery()/UseAntiforgery(), same trust model as the
        // rest of the API: no auth middleware, callerUserId is caller-asserted).
        .DisableAntiforgery();

        // ── List documents shared in a conversation ────────────────────────
        app.MapGet("/api/conversations/{conversationId}/documents", async (
            string conversationId, string callerUserId, DoctorCommunicationsDal dal, CancellationToken ct) =>
        {
            var (isParticipant, documents) = await dal.GetConversationDocumentsAsync(conversationId, callerUserId, ct);
            if (!isParticipant) return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.Ok(documents);
        });

        // ── Download a document ─────────────────────────────────────────────
        app.MapGet("/api/documents/{documentId:long}/download", async (
            long documentId, string callerUserId, DoctorCommunicationsDal dal, IDocumentStorageService storage, CancellationToken ct) =>
        {
            var document = await dal.GetSharedDocumentAsync(documentId, ct);
            if (document is null) return Results.NotFound();

            var isParticipant = await dal.IsAcceptedParticipantAsync(document.ConversationId, callerUserId, ct);
            if (!isParticipant) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var buffer = new MemoryStream();
            await storage.DownloadAsync(document.ObjectKey, buffer, ct);
            buffer.Seek(0, SeekOrigin.Begin);

            return Results.File(buffer, document.ContentType, SanitizeFileName(document.FileName));
        });
    }

    // Strips characters that could break the Content-Disposition header (CR/LF
    // header injection) or be misread as a path when the browser saves the file.
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace("\r", "").Replace("\n", ""));
        return string.IsNullOrWhiteSpace(name) ? "document" : name;
    }
}
