// DoctorCommunicationsDal.cs
// ADO.NET data-access layer for doctor-to-doctor chat. Every operation is a
// stored procedure in DoctorCommunications_Schema.sql — no EF Core.
// Stateless (connection per call, pooled by SqlClient) → safe as a singleton.
using System.Data;
using Microsoft.Data.SqlClient;

public class DoctorCommunicationsDal
{
    private const string ParticipantListType = "dbo.DoctorCommunications_ParticipantList";
    private readonly string _connectionString;

    public DoctorCommunicationsDal(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DoctorCommunicationsDb")
            ?? throw new InvalidOperationException("Connection string 'DoctorCommunicationsDb' is missing.");
    }

    // ── Reads ─────────────────────────────────────────────────────────────

    /// <summary>PR_DoctorComm_GetPendingInvites</summary>
    public async Task<List<PendingInviteDto>> GetPendingInvitesAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_GetPendingInvites");
        cmd.Parameters.Add("@UserId", SqlDbType.NVarChar, 450).Value = userId;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var invites = new List<PendingInviteDto>();
        var byId = new Dictionary<string, PendingInviteDto>();

        // Result set 1 — invites
        while (await reader.ReadAsync(ct))
        {
            var invite = new PendingInviteDto(
                ConversationId: reader.GetString("ConversationId"),
                StarterUserId:  reader.GetString("StarterUserId"),
                StarterName:    GetNullableString(reader, "StarterName"),
                CreatedAtUtc:   AsUtc(reader.GetDateTime("CreatedAtUtc")),
                Participants:   new List<ParticipantDto>());
            invites.Add(invite);
            byId[invite.ConversationId] = invite;
        }

        // Result set 2 — participants
        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (byId.TryGetValue(reader.GetString("ConversationId"), out var invite))
                    invite.Participants.Add(new ParticipantDto(reader.GetString("UserId"), reader.GetString("Name"), GetNullableString(reader, "PhotoPath")));
            }
        }

        return invites;
    }

    /// <summary>PR_DoctorComm_GetMyConversations</summary>
    public async Task<List<MyConversationDto>> GetMyConversationsAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_GetMyConversations");
        cmd.Parameters.Add("@UserId", SqlDbType.NVarChar, 450).Value = userId;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var conversations = new List<MyConversationDto>();
        var byId = new Dictionary<string, MyConversationDto>();

        // Result set 1 — conversations (newest activity first)
        while (await reader.ReadAsync(ct))
        {
            var lastAt = GetNullableDateTime(reader, "LastMessageAtUtc");
            var conv = new MyConversationDto(
                ConversationId:  reader.GetString("ConversationId"),
                CreatedAtUtc:    AsUtc(reader.GetDateTime("CreatedAtUtc")),
                LastMessageAt:   lastAt.HasValue ? AsUtc(lastAt.Value) : null,
                LastMessageText: GetNullableString(reader, "LastMessageText"),
                Participants:    new List<ParticipantDto>());
            conversations.Add(conv);
            byId[conv.ConversationId] = conv;
        }

        // Result set 2 — accepted participants
        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (byId.TryGetValue(reader.GetString("ConversationId"), out var conv))
                    conv.Participants.Add(new ParticipantDto(
                        reader.GetString("UserId"),
                        reader.GetString("Name"),
                        GetNullableString(reader, "PhotoPath"),
                        GetNullableString(reader, "DepartmentName"),
                        HasColumn(reader, "IsOnline") && !reader.IsDBNull(reader.GetOrdinal("IsOnline"))
                            ? reader.GetBoolean(reader.GetOrdinal("IsOnline"))
                            : null));
            }
        }

        return conversations;
    }

    /// <summary>
    /// PR_DoctorComm_GetConversationMessages.
    /// IsParticipant = false → caller isn't an accepted participant (API returns 403).
    /// </summary>
    public async Task<(bool IsParticipant, List<ChatMessageDto> Messages)> GetMessagesAsync(
        string conversationId, string callerUserId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_GetConversationMessages");
        cmd.Parameters.Add("@ConversationId", SqlDbType.NVarChar, 450).Value = conversationId;
        cmd.Parameters.Add("@CallerUserId", SqlDbType.NVarChar, 450).Value = callerUserId;
        var isParticipantParam = cmd.Parameters.Add("@IsParticipant", SqlDbType.Bit);
        isParticipantParam.Direction = ParameterDirection.Output;

        await conn.OpenAsync(ct);

        var messages = new List<ChatMessageDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                messages.Add(new ChatMessageDto(
                    Id:           reader.GetInt64("Id"),
                    SenderUserId: reader.GetString("SenderUserId"),
                    SenderName:   reader.GetString("SenderName"),
                    Text:         reader.GetString("Text"),
                    Timestamp:    AsUtc(reader.GetDateTime("SentAtUtc")),
                    PhotoPath:    GetNullableString(reader, "PhotoPath")));
            }
        } // reader must be closed before OUTPUT params are populated

        bool isParticipant = isParticipantParam.Value is bool b && b;
        return (isParticipant, messages);
    }

    /// <summary>PR_DoctorComm_GetConversationParticipants (all statuses).</summary>
    public async Task<List<ParticipantStatusDto>> GetParticipantsAsync(string conversationId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_GetConversationParticipants");
        cmd.Parameters.Add("@ConversationId", SqlDbType.NVarChar, 450).Value = conversationId;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ReadParticipantStatusesAsync(reader, ct);
    }

    /// <summary>PR_DoctorComm_IsAcceptedParticipant</summary>
    public async Task<bool> IsAcceptedParticipantAsync(string conversationId, string userId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_IsAcceptedParticipant");
        cmd.Parameters.Add("@ConversationId", SqlDbType.NVarChar, 450).Value = conversationId;
        cmd.Parameters.Add("@UserId", SqlDbType.NVarChar, 450).Value = userId;
        var outParam = cmd.Parameters.Add("@IsParticipant", SqlDbType.Bit);
        outParam.Direction = ParameterDirection.Output;

        await conn.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
        return outParam.Value is bool b && b;
    }

    // ── Writes ────────────────────────────────────────────────────────────

    /// <summary>
    /// PR_DoctorComm_CreateConversation. Starter → Accepted, invitees → Pending.
    /// Pass conversationId = null to let SQL generate one.
    /// </summary>
    public async Task<CreateConversationResult> CreateConversationAsync(
        string? conversationId,
        string createdByUserId,
        string createdByName,
        IEnumerable<ParticipantDto> invitees,
        CancellationToken ct = default)
    {
        var tvp = new DataTable();
        tvp.Columns.Add("UserId", typeof(string));
        tvp.Columns.Add("Name", typeof(string));
        foreach (var i in invitees)
            tvp.Rows.Add(i.UserId, i.Name ?? "");

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_CreateConversation");

        var idParam = cmd.Parameters.Add("@ConversationId", SqlDbType.NVarChar, 450);
        idParam.Direction = ParameterDirection.InputOutput;
        idParam.Value = string.IsNullOrWhiteSpace(conversationId) ? DBNull.Value : conversationId;

        cmd.Parameters.Add("@CreatedByUserId", SqlDbType.NVarChar, 450).Value = createdByUserId;
        cmd.Parameters.Add("@CreatedByName", SqlDbType.NVarChar, 200).Value = createdByName ?? "";

        var tvpParam = cmd.Parameters.AddWithValue("@Invitees", tvp);
        tvpParam.SqlDbType = SqlDbType.Structured;
        tvpParam.TypeName = ParticipantListType;

        await conn.OpenAsync(ct);

        List<ParticipantStatusDto> participants;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            participants = await ReadParticipantStatusesAsync(reader, ct);
        }

        // NULL when the SP rejected the request (e.g. self chat / no other doctor → RETURN 3)
        var createdId = idParam.Value as string ?? "";
        return new CreateConversationResult(createdId, participants);
    }

    /// <summary>PR_DoctorComm_RespondToInvite (only changes a Pending row).</summary>
    public async Task<RespondToInviteResult> RespondToInviteAsync(
        string conversationId, string userId, bool accept, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_RespondToInvite");
        cmd.Parameters.Add("@ConversationId", SqlDbType.NVarChar, 450).Value = conversationId;
        cmd.Parameters.Add("@UserId", SqlDbType.NVarChar, 450).Value = userId;
        cmd.Parameters.Add("@Accept", SqlDbType.Bit).Value = accept;
        var updatedParam = cmd.Parameters.Add("@Updated", SqlDbType.Bit);
        updatedParam.Direction = ParameterDirection.Output;

        await conn.OpenAsync(ct);

        List<ParticipantStatusDto> participants;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            participants = await ReadParticipantStatusesAsync(reader, ct);
        }

        bool updated = updatedParam.Value is bool b && b;
        return new RespondToInviteResult(updated, participants);
    }

    /// <summary>PR_DoctorComm_SaveMessage (sender must be Accepted).</summary>
    public async Task<SaveMessageResult> SaveMessageAsync(
        string conversationId, string senderUserId, string senderName, string text, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_SaveMessage");
        cmd.Parameters.Add("@ConversationId", SqlDbType.NVarChar, 450).Value = conversationId;
        cmd.Parameters.Add("@SenderUserId", SqlDbType.NVarChar, 450).Value = senderUserId;
        cmd.Parameters.Add("@SenderName", SqlDbType.NVarChar, 200).Value = senderName ?? "";
        cmd.Parameters.Add("@Text", SqlDbType.NVarChar, -1).Value = (object?)text ?? DBNull.Value;

        var idParam = cmd.Parameters.Add("@MessageId", SqlDbType.BigInt);
        idParam.Direction = ParameterDirection.Output;
        var sentParam = cmd.Parameters.Add("@SentAtUtc", SqlDbType.DateTime2);
        sentParam.Precision = 3;
        sentParam.Direction = ParameterDirection.Output;
        var returnParam = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnParam.Direction = ParameterDirection.ReturnValue;

        await conn.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);

        var status = (SaveMessageStatus)(returnParam.Value is int rc ? rc : (int)SaveMessageStatus.NotParticipant);
        if (status != SaveMessageStatus.Saved)
            return new SaveMessageResult(status, null, null);

        return new SaveMessageResult(
            status,
            idParam.Value is long id ? id : null,
            sentParam.Value is DateTime dt ? AsUtc(dt) : null);
    }

    // ── Favorites ─────────────────────────────────────────────────────────

    /// <summary>PR_DoctorComm_GetFavorites</summary>
    public async Task<List<FavoriteDto>> GetFavoritesAsync(string ownerUserId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_GetFavorites");
        cmd.Parameters.Add("@OwnerUserId", SqlDbType.NVarChar, 450).Value = ownerUserId;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<FavoriteDto>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadFavorite(reader));
        return list;
    }

    /// <summary>
    /// PR_DoctorComm_AddFavorite (upsert). Returns the saved favorite, or null
    /// with a non-Saved status (self-favorite / missing ids).
    /// </summary>
    public async Task<(AddFavoriteStatus Status, FavoriteDto? Favorite)> AddFavoriteAsync(
        AddFavoriteRequest req, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_AddFavorite");
        cmd.Parameters.Add("@OwnerUserId", SqlDbType.NVarChar, 450).Value = (object?)req.OwnerUserId ?? DBNull.Value;
        cmd.Parameters.Add("@FavoriteUserId", SqlDbType.NVarChar, 450).Value = (object?)req.FavoriteUserId ?? DBNull.Value;
        cmd.Parameters.Add("@FavoriteName", SqlDbType.NVarChar, 200).Value = req.Name ?? "";
        cmd.Parameters.Add("@PhotoPath", SqlDbType.NVarChar, 500).Value = (object?)req.PhotoPath ?? DBNull.Value;
        cmd.Parameters.Add("@Designation", SqlDbType.NVarChar, 300).Value = (object?)req.Designation ?? DBNull.Value;
        var returnParam = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnParam.Direction = ParameterDirection.ReturnValue;

        await conn.OpenAsync(ct);

        FavoriteDto? saved = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                saved = ReadFavorite(reader);
        } // reader must be closed before the return value is populated

        var status = (AddFavoriteStatus)(returnParam.Value is int rc ? rc : (int)AddFavoriteStatus.MissingIds);
        return (status, status == AddFavoriteStatus.Saved ? saved : null);
    }

    /// <summary>PR_DoctorComm_RemoveFavorite — true if a row was deleted.</summary>
    public async Task<bool> RemoveFavoriteAsync(string ownerUserId, string favoriteUserId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_RemoveFavorite");
        cmd.Parameters.Add("@OwnerUserId", SqlDbType.NVarChar, 450).Value = ownerUserId;
        cmd.Parameters.Add("@FavoriteUserId", SqlDbType.NVarChar, 450).Value = favoriteUserId;
        var removedParam = cmd.Parameters.Add("@Removed", SqlDbType.Bit);
        removedParam.Direction = ParameterDirection.Output;

        await conn.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
        return removedParam.Value is bool b && b;
    }

    private static FavoriteDto ReadFavorite(SqlDataReader reader) => new(
        FavoriteUserId: reader.GetString("FavoriteUserId"),
        Name:           reader.GetString("Name"),
        PhotoPath:      GetNullableString(reader, "PhotoPath"),
        Designation:    GetNullableString(reader, "Designation"),
        CreatedAtUtc:   AsUtc(reader.GetDateTime("CreatedAtUtc")));

    // ── Online status ─────────────────────────────────────────────────────

    /// <summary>PR_DoctorComm_GetUsersOnlineStatus (latest login row per user).</summary>
    public async Task<List<UserOnlineStatusDto>> GetUsersOnlineStatusAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var csv = string.Join(",", userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct());

        var list = new List<UserOnlineStatusDto>();
        if (csv.Length == 0) return list;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Sp(conn, "dbo.PR_DoctorComm_GetUsersOnlineStatus");
        cmd.Parameters.Add("@UserIds", SqlDbType.NVarChar, -1).Value = csv;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new UserOnlineStatusDto(
                UserId:       reader.GetString("UserId"),
                IsOnline:     reader.GetBoolean("IsOnline"),
                LastLoginAt:  GetNullableDateTime(reader, "LastLoginAt"),
                LastLogoutAt: GetNullableDateTime(reader, "LastLogoutAt")));
        }
        return list;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool HasColumn(SqlDataReader r, string col)
    {
        for (int i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static SqlCommand Sp(SqlConnection conn, string name) =>
        new(name, conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };

    private static async Task<List<ParticipantStatusDto>> ReadParticipantStatusesAsync(SqlDataReader reader, CancellationToken ct)
    {
        var list = new List<ParticipantStatusDto>();
        while (await reader.ReadAsync(ct))
        {
            var status = Enum.TryParse<ParticipantStatus>(reader.GetString("Status"), out var s) ? s : ParticipantStatus.Pending;
            list.Add(new ParticipantStatusDto(reader.GetString("UserId"), reader.GetString("Name"), status, GetNullableString(reader, "PhotoPath")));
        }
        return list;
    }

    private static string? GetNullableString(SqlDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }

    // SQL returns DATETIME2 as Kind=Unspecified; mark it UTC so JSON gets a trailing "Z"
    // and the browser converts it to local (Riyadh) time correctly.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
