// ChatModels.cs
// DTOs for doctor-to-doctor chat. EF Core entities removed — persistence is now
// handled by stored procedures (see DoctorCommunications_Schema.sql) through
// DoctorCommunicationsDal. Property names serialize to camelCase, so the JSON
// shape returned by the REST endpoints is unchanged for the Angular client.

public enum ParticipantStatus
{
    Pending,
    Accepted,
    Declined
}

/// <summary>Participant as returned to the client (userId + name).</summary>
public record ParticipantDto(string UserId, string Name, string? PhotoPath = null);

/// <summary>Participant with invite status — used server-side (hub fan-out).</summary>
public record ParticipantStatusDto(string UserId, string Name, ParticipantStatus Status, string? PhotoPath = null);

/// <summary>GET /api/conversations/pending/{userId}</summary>
public record PendingInviteDto(
    string ConversationId,
    string StarterUserId,
    string? StarterName,
    DateTime CreatedAtUtc,
    List<ParticipantDto> Participants);

/// <summary>GET /api/conversations/mine/{userId}</summary>
public record MyConversationDto(
    string ConversationId,
    DateTime CreatedAtUtc,
    DateTime? LastMessageAt,
    string? LastMessageText,
    List<ParticipantDto> Participants);

/// <summary>GET /api/conversations/{conversationId}/messages</summary>
public record ChatMessageDto(
    long Id,
    string SenderUserId,
    string SenderName,
    string Text,
    DateTime Timestamp,
    string? PhotoPath = null);

/// <summary>Result of PR_DoctorComm_CreateConversation.</summary>
public record CreateConversationResult(string ConversationId, List<ParticipantStatusDto> Participants);

/// <summary>Result of PR_DoctorComm_RespondToInvite.</summary>
public record RespondToInviteResult(bool Updated, List<ParticipantStatusDto> Participants);

/// <summary>Result of PR_DoctorComm_SaveMessage.</summary>
public enum SaveMessageStatus
{
    Saved = 0,
    NotParticipant = 1,
    EmptyText = 2
}

public record SaveMessageResult(SaveMessageStatus Status, long? MessageId, DateTime? SentAtUtc);

// ── Favorite doctors ─────────────────────────────────────────────────────

/// <summary>GET /api/favorites/{ownerUserId} — one favorite doctor.</summary>
public record FavoriteDto(
    string FavoriteUserId,
    string Name,
    string? PhotoPath,
    string? Designation,
    DateTime CreatedAtUtc);

/// <summary>POST /api/favorites body.</summary>
public record AddFavoriteRequest(
    string OwnerUserId,
    string FavoriteUserId,
    string Name,
    string? PhotoPath = null,
    string? Designation = null);

/// <summary>Result of PR_DoctorComm_AddFavorite.</summary>
public enum AddFavoriteStatus
{
    Saved = 0,
    SelfFavorite = 1,
    MissingIds = 2
}
