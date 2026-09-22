using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

public class CallHub : Hub
{
    private readonly ConnectionStore _connections;
    private readonly DoctorCommunicationsDbContext _db;
    private readonly ILogger<CallHub> _logger;

    public CallHub(ConnectionStore connections, DoctorCommunicationsDbContext db, ILogger<CallHub> logger)
    {
        _connections = connections;
        _db = db;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Hub connected: connectionId={ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    // Angular (doctor side, tele-icu.component.ts): after joining the Agora
    // channel, the doctor tells the nurse "I joined" so her UI can advance
    // from 'calling' to 'in-call'.
    public async Task NotifyDoctorJoined(string nurseConnectionId, string doctorName)
    {
        await Clients.Client(nurseConnectionId).SendAsync("DoctorJoined", new { doctorName });
    }

    public async Task DeclineCall(string nurseConnectionId)
    {
        await Clients.Client(nurseConnectionId).SendAsync("CallDeclined");
    }

    // ── Presence ─────────────────────────────────────────────────────────

    public async Task RegisterUser(string userId)
    {
        _logger.LogInformation("RegisterUser: userId={UserId} connectionId={ConnectionId}", userId, Context.ConnectionId);

        _connections.Register(userId, Context.ConnectionId);

        // Rejoin every ACCEPTED chat group this user belongs to — SignalR drops
        // group membership on page refresh / automatic reconnect. Pending
        // invites are re-delivered separately via GET /api/conversations/pending/{userId},
        // which the frontend calls on load instead of relying on a live push.
        var conversationIds = await _db.ConversationParticipants
            .Where(p => p.UserId == userId && p.Status == ParticipantStatus.Accepted)
            .Select(p => p.ConversationId)
            .ToListAsync();

        _logger.LogInformation("RegisterUser: userId={UserId} rejoining {Count} conversation group(s)", userId, conversationIds.Count);

        foreach (var conversationId in conversationIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversationId));
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(exception, "Hub disconnected: connectionId={ConnectionId}", Context.ConnectionId);
        _connections.RemoveByConnectionId(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // ── Doctor-to-doctor chat ────────────────────────────────────────────
    // Flow: StartChat creates the conversation and auto-accepts the starter,
    // but every invitee is Pending until they call AcceptChat/DeclineChat —
    // they only join the SignalR group (and start receiving messages) once
    // accepted. Declining never creates a conversation on the decliner's own
    // side; the starter/other accepted members just get told who declined.

    public async Task<string> StartChat(
        string[] participantUserIds,
        string[] participantNames,
        string starterUserId,
        string starterName)
    {
        _logger.LogInformation(
            "StartChat: starterUserId={StarterUserId} starterName={StarterName} participantUserIds=[{ParticipantUserIds}]",
            starterUserId, starterName, string.Join(", ", participantUserIds));

        var conversationId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var participants = new List<ChatParticipantEntity>
        {
            new() { ConversationId = conversationId, UserId = starterUserId, Name = starterName, Status = ParticipantStatus.Accepted, RespondedAtUtc = now }
        };
        for (int i = 0; i < participantUserIds.Length; i++)
        {
            var name = i < participantNames.Length ? participantNames[i] : participantUserIds[i];
            participants.Add(new ChatParticipantEntity { ConversationId = conversationId, UserId = participantUserIds[i], Name = name, Status = ParticipantStatus.Pending });
        }

        _db.Conversations.Add(new ChatConversationEntity { Id = conversationId, CreatedByUserId = starterUserId, CreatedAtUtc = now });
        _db.ConversationParticipants.AddRange(participants);
        await _db.SaveChangesAsync();

        var group = GroupName(conversationId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        var invitePayload = new
        {
            conversationId,
            starterUserId,
            starterName,
            participants = participants.Select(p => new { userId = p.UserId, name = p.Name })
        };

        foreach (var userId in participantUserIds)
        {
            var connectionId = _connections.Get(userId);
            if (connectionId is null)
            {
                _logger.LogWarning("StartChat: invitee userId={UserId} has NO registered connection — will see it via pending-invites lookup next time they connect", userId);
                continue;
            }

            _logger.LogInformation("StartChat: inviting userId={UserId} connectionId={ConnectionId}", userId, connectionId);
            await Clients.Client(connectionId).SendAsync("ChatInvite", invitePayload);
        }

        return conversationId;
    }

    public async Task AcceptChat(string conversationId, string userId)
    {
        var participant = await _db.ConversationParticipants
            .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);
        if (participant is null)
        {
            _logger.LogWarning("AcceptChat: no participant row for conversationId={ConversationId} userId={UserId}", conversationId, userId);
            return;
        }

        participant.Status = ParticipantStatus.Accepted;
        participant.RespondedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var group = GroupName(conversationId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        _logger.LogInformation("AcceptChat: userId={UserId} joined conversationId={ConversationId}", userId, conversationId);

        await Clients.OthersInGroup(group).SendAsync("ChatMemberJoined", new { conversationId, userId, name = participant.Name });
    }

    public async Task DeclineChat(string conversationId, string userId)
    {
        var participant = await _db.ConversationParticipants
            .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);
        if (participant is null)
        {
            _logger.LogWarning("DeclineChat: no participant row for conversationId={ConversationId} userId={UserId}", conversationId, userId);
            return;
        }

        participant.Status = ParticipantStatus.Declined;
        participant.RespondedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("DeclineChat: userId={UserId} declined conversationId={ConversationId}", userId, conversationId);

        // The decliner was never added to the group, so broadcast to the
        // group directly (not OthersInGroup) — everyone already in it
        // (starter + anyone who already accepted) should be told.
        await Clients.Group(GroupName(conversationId))
            .SendAsync("ChatMemberDeclined", new { conversationId, userId, name = participant.Name });
    }

    public async Task SendChatMessage(string conversationId, string senderUserId, string senderName, string text)
    {
        var group = GroupName(conversationId);
        _logger.LogInformation(
            "SendChatMessage: conversationId={ConversationId} senderUserId={SenderUserId} group={Group}",
            conversationId, senderUserId, group);

        var message = new ChatMessageEntity
        {
            ConversationId = conversationId,
            SenderUserId = senderUserId,
            SenderName = senderName,
            Text = text,
            SentAtUtc = DateTime.UtcNow
        };
        _db.ChatMessages.Add(message);
        await _db.SaveChangesAsync();

        await Clients.GroupExcept(group, new[] { Context.ConnectionId }).SendAsync("ChatMessageReceived", new
        {
            conversationId,
            senderUserId,
            senderName,
            text,
            timestamp = message.SentAtUtc
        });
    }

    private static string GroupName(string conversationId) => $"chat-{conversationId}";
}
