// ChatModels.cs
// EF Core entities for persisted doctor-to-doctor chat history.
// Tables are prefixed DoctorCommunications_ so they stay clearly separated from the
// hospital's core HIS schema even though they live in the same database.

public enum ParticipantStatus
{
    Pending,
    Accepted,
    Declined
}

public class ChatConversationEntity
{
    public string Id { get; set; } = "";
    public string CreatedByUserId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }

    public List<ChatParticipantEntity> Participants { get; set; } = new();
    public List<ChatMessageEntity> Messages { get; set; } = new();
}

public class ChatParticipantEntity
{
    public int Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public ParticipantStatus Status { get; set; }
    public DateTime? RespondedAtUtc { get; set; }

    public ChatConversationEntity? Conversation { get; set; }
}

public class ChatMessageEntity
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string SenderUserId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentAtUtc { get; set; }

    public ChatConversationEntity? Conversation { get; set; }
}
