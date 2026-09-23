// Program.cs
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR(options =>
{
    // Dev-only: surface real exception messages to the client instead of a
    // generic "Failed to invoke" error, to make hub-method failures debuggable.
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSingleton<ConnectionStore>();

// Chat persistence: stored procedures via ADO.NET (EF Core removed).
// Uses ConnectionStrings:DoctorCommunicationsDb. Stateless → singleton.
builder.Services.AddSingleton<DoctorCommunicationsDal>();

// Allowed origins come from config (Cors:AllowedOrigins in appsettings*.json)
// instead of being hardcoded, so deploying just needs the real origin(s) the
// Angular app is served from added to appsettings.json / appsettings.Production.json
// — no code change per environment. AllowCredentials() means this can never
// be "*"; SignalR's negotiate + WebSocket handshake enforce the same origin
// check as any other request, so a missing entry here breaks the hub connection too.
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// ── Middleware ─────────────────────────────────────────────────────────
// Swagger enabled for Development and UAT — NOT just IsDevelopment() —
// since this app is currently running under ASPNETCORE_ENVIRONMENT=UAT
// (a machine-level env var overriding launchSettings.json on some boxes),
// and Swagger should stay reachable there without needing that var changed.
// Still excluded from Production/Staging etc. Adjust the name list if your
// UAT environment is named differently.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("UAT"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAngular");

// ── SignalR Hub ────────────────────────────────────────────────────────
app.MapHub<CallHub>("/hubs/call");

// ── REST: register a user's connectionId ──────────────────────────────
// (kept for HTTP-only flows; prefer SignalR RegisterUser method instead)
app.MapPost("/api/users/register-connection", (ConnectionRequest req, ConnectionStore store, ILogger<Program> logger) =>
{
    logger.LogInformation("REST register-connection: userId={UserId} connectionId={ConnectionId}", req.UserId, req.ConnectionId);
    store.Register(req.UserId, req.ConnectionId);
    return Results.Ok();
});

// ── REST: look up a user's current connectionId ───────────────────────
app.MapGet("/api/users/connection/{userId}", (string userId, ConnectionStore store, ILogger<Program> logger) =>
{
    var connectionId = store.Get(userId);
    logger.LogInformation("REST connection lookup: userId={UserId} -> {ConnectionId}", userId, connectionId ?? "(not found)");
    return connectionId is null
        ? Results.NotFound()
        : Results.Ok(new { connectionId });
});

// ── REST: chat invites this user hasn't responded to yet ──────────────
// The frontend calls this on load/reconnect instead of relying on a live
// ChatInvite push, which only reaches a doctor who's already connected at
// the moment StartChat runs.
app.MapGet("/api/conversations/pending/{userId}", async (string userId, DoctorCommunicationsDal dal, CancellationToken ct) =>
{
    var pending = await dal.GetPendingInvitesAsync(userId, ct);
    return Results.Ok(pending);
});

// ── REST: this user's accepted conversations (for restoring the chat list
// on page reload — the in-memory Angular state is gone after a refresh) ──
app.MapGet("/api/conversations/mine/{userId}", async (string userId, DoctorCommunicationsDal dal, CancellationToken ct) =>
{
    var conversations = await dal.GetMyConversationsAsync(userId, ct);
    return Results.Ok(conversations);
});

// ── REST: message history for a conversation ───────────────────────────
// Only returns history if callerUserId is actually an accepted participant
// — same trust model as the rest of this API (userId is caller-asserted,
// no auth middleware here yet), but at least scopes history to conversations
// you were actually let into.
app.MapGet("/api/conversations/{conversationId}/messages", async (string conversationId, string callerUserId, DoctorCommunicationsDal dal, CancellationToken ct) =>
{
    var (isParticipant, messages) = await dal.GetMessagesAsync(conversationId, callerUserId, ct);
    if (!isParticipant) return Results.StatusCode(StatusCodes.Status403Forbidden);

    return Results.Ok(messages);
});

// ── REST: nurse initiates a call ──────────────────────────────────────
// Notifies every selected doctor who's currently online. Channel name is
// deterministic per patient (not random) so the nurse and every doctor who
// accepts — possibly at different times — land in the same Agora room;
// doctor-call.component.ts joins Agora using `local-player-${patient.SSN}`
// element ids, which assumes exactly this.
app.MapPost("/api/call/start", async (
    CallRequest req,
    IHubContext<CallHub> hub,
    ConnectionStore store) =>
{
    if (req.DoctorIds is null || req.DoctorIds.Count == 0)
        return Results.BadRequest("No doctors selected.");

    string channelName = $"icu-{req.PatientSsn.Replace("-", "").ToLower()}";

    int notified = 0;
    foreach (var doctorId in req.DoctorIds)
    {
        var connectionId = store.Get(doctorId.ToString());
        if (connectionId is null) continue; // doctor not online — skip silently

        await hub.Clients.Client(connectionId).SendAsync("IncomingCall", new
        {
            channelName,
            callerName        = req.NurseName,
            patientSsn        = req.PatientSsn,
            nurseConnectionId = req.NurseConnectionId
        });
        notified++;
    }

    return Results.Ok(new { channelName, doctorsNotified = notified });
});

// ── REST: generate an Agora RTC token ─────────────────────────────────
app.MapGet("/api/agora/token", (string channelName, uint uid) =>
{
    // ⚠ Replace with real values from console.agora.io
    string appId          = builder.Configuration["Agora:AppId"]          ?? "";
    string appCertificate = builder.Configuration["Agora:AppCertificate"] ?? "";

    var token = AgoraTokenService.GenerateToken(appId, appCertificate, channelName, uid);
    return Results.Ok(new { token });
});

app.Run();

// ─────────────────────────────────────────────────────────────────────
// Models & helpers
// ─────────────────────────────────────────────────────────────────────

public record CallRequest(string PatientSsn, string NurseName, string NurseConnectionId, List<int> DoctorIds);
public record ConnectionRequest(string UserId, string ConnectionId);

public class ConnectionStore
{
    private readonly Dictionary<string, string> _userToConn = new();
    private readonly Dictionary<string, string> _connToUser = new();
    private readonly object _lock = new();

    public void Register(string userId, string connectionId)
    {
        lock (_lock)
        {
            _userToConn[userId]       = connectionId;
            _connToUser[connectionId] = userId;
        }
    }

    public string? Get(string userId)
    {
        lock (_lock)
        {
            return _userToConn.GetValueOrDefault(userId);
        }
    }

    public void RemoveByConnectionId(string connectionId)
    {
        lock (_lock)
        {
            if (_connToUser.TryGetValue(connectionId, out var userId))
            {
                _userToConn.Remove(userId);
                _connToUser.Remove(connectionId);
            }
        }
    }
}