using Microsoft.AspNetCore.SignalR;

public class CallHub : Hub
{
    private readonly ConnectionStore _connections;
    private readonly DoctorCommunicationsDal _dal;
    private readonly ILogger<CallHub> _logger;

    public CallHub(ConnectionStore connections, DoctorCommunicationsDal dal, ILogger<CallHub> logger)
    {
        _connections = connections;
        _dal = dal;
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
        var conversations = await _dal.GetMyConversationsAsync(userId);

        _logger.LogInformation("RegisterUser: userId={UserId} rejoining {Count} conversation group(s)", userId, conversations.Count);

        foreach (var conversation in conversations)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversation.ConversationId));
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

        var invitees = new List<ParticipantDto>();
        for (int i = 0; i < participantUserIds.Length; i++)
        {
            // Self chat is not allowed — skip the starter and blank ids
            if (string.IsNullOrWhiteSpace(participantUserIds[i]) || participantUserIds[i] == starterUserId)
                continue;

            var name = i < participantNames.Length ? participantNames[i] : participantUserIds[i];
            invitees.Add(new ParticipantDto(participantUserIds[i], name, PhotoPath: null)); // photo not sent by StartChat — read back from DB
        }

        if (invitees.Count == 0)
            throw new HubException("Select at least one other doctor to start a chat.");

        // For a 1:1 chat the stored procedure returns the EXISTING conversation
        // with this doctor (if any) instead of creating a duplicate.
        var result = await _dal.CreateConversationAsync(
            conversationId: null,
            createdByUserId: starterUserId,
            createdByName: starterName,
            invitees: invitees);

        if (string.IsNullOrEmpty(result.ConversationId))
            throw new HubException("Chat could not be started.");

        var group = GroupName(result.ConversationId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        var invitePayload = new
        {
            conversationId = result.ConversationId,
            starterUserId,
            starterName,
            participants = result.Participants.Select(p => new { userId = p.UserId, name = p.Name, photoPath = p.PhotoPath })
        };

        // De-duplicated by the stored procedure — iterate what actually got
        // persisted (Pending rows) rather than the raw, possibly-duplicate
        // input arrays.
        foreach (var invitee in result.Participants.Where(p => p.Status == ParticipantStatus.Pending))
        {
            var connectionId = _connections.Get(invitee.UserId);
            if (connectionId is null)
            {
                _logger.LogWarning("StartChat: invitee userId={UserId} has NO registered connection — will see it via pending-invites lookup next time they connect", invitee.UserId);
                continue;
            }

            _logger.LogInformation("StartChat: inviting userId={UserId} connectionId={ConnectionId}", invitee.UserId, connectionId);
            await Clients.Client(connectionId).SendAsync("ChatInvite", invitePayload);
        }

        return result.ConversationId;
    }

    public async Task AcceptChat(string conversationId, string userId)
    {
        var result = await _dal.RespondToInviteAsync(conversationId, userId, accept: true);
        if (!result.Updated)
        {
            _logger.LogWarning("AcceptChat: no Pending row to update for conversationId={ConversationId} userId={UserId}", conversationId, userId);
            return;
        }

        var group = GroupName(conversationId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        var member = result.Participants.FirstOrDefault(p => p.UserId == userId);
        var name = member?.Name ?? "";
        var photoPath = member?.PhotoPath;
        _logger.LogInformation("AcceptChat: userId={UserId} joined conversationId={ConversationId}", userId, conversationId);

        await Clients.OthersInGroup(group).SendAsync("ChatMemberJoined", new { conversationId, userId, name, photoPath });
    }

    public async Task DeclineChat(string conversationId, string userId)
    {
        var result = await _dal.RespondToInviteAsync(conversationId, userId, accept: false);
        if (!result.Updated)
        {
            _logger.LogWarning("DeclineChat: no Pending row to update for conversationId={ConversationId} userId={UserId}", conversationId, userId);
            return;
        }

        var name = result.Participants.FirstOrDefault(p => p.UserId == userId)?.Name ?? "";
        _logger.LogInformation("DeclineChat: userId={UserId} declined conversationId={ConversationId}", userId, conversationId);

        // The decliner was never added to the group, so broadcast to the
        // group directly (not OthersInGroup) — everyone already in it
        // (starter + anyone who already accepted) should be told.
        await Clients.Group(GroupName(conversationId))
            .SendAsync("ChatMemberDeclined", new { conversationId, userId, name });
    }

    public async Task SendChatMessage(string conversationId, string senderUserId, string senderName, string text)
    {
        var group = GroupName(conversationId);
        _logger.LogInformation(
            "SendChatMessage: conversationId={ConversationId} senderUserId={SenderUserId} group={Group}",
            conversationId, senderUserId, group);

        var result = await _dal.SaveMessageAsync(conversationId, senderUserId, senderName, text);
        if (result.Status != SaveMessageStatus.Saved)
        {
            _logger.LogWarning(
                "SendChatMessage: not saved (status={Status}) conversationId={ConversationId} senderUserId={SenderUserId}",
                result.Status, conversationId, senderUserId);
            return;
        }

        await Clients.GroupExcept(group, new[] { Context.ConnectionId }).SendAsync("ChatMessageReceived", new
        {
            conversationId,
            senderUserId,
            senderName,
            text,
            timestamp = result.SentAtUtc
        });
    }

    private static string GroupName(string conversationId) => $"chat-{conversationId}";
}
