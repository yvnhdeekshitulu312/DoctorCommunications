/* ════════════════════════════════════════════════════════════════════════════
   DoctorCommunications — per-user read state / unread counts
   ----------------------------------------------------------------------------
   • Stores how far each participant has read each conversation, so the unread
     badge survives logout / login / another device. Previously this lived only
     in the browser's localStorage, which every logout clears.
   • Kept in its own table + its own procedures on purpose: nothing here alters
     an existing table or procedure (PR_DoctorComm_GetMyConversations etc. are
     untouched — the API merges the unread counts in).
   • Re-runnable: table is created only if missing, procedures use CREATE OR ALTER.
   • All timestamps are UTC (DATETIME2(3)).
   ════════════════════════════════════════════════════════════════════════════ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ─────────────────────────────────────────────────────────────────────────────
   1. TABLE
   ───────────────────────────────────────────────────────────────────────────── */

IF OBJECT_ID(N'dbo.DoctorCommunications_ReadState', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DoctorCommunications_ReadState
    (
        ConversationId  NVARCHAR(450) NOT NULL,
        UserId          NVARCHAR(450) NOT NULL,
        LastReadAtUtc   DATETIME2(3)  NOT NULL,

        CONSTRAINT PK_DoctorCommunications_ReadState PRIMARY KEY CLUSTERED (UserId, ConversationId)
    );
END
GO

/* ─────────────────────────────────────────────────────────────────────────────
   2. STORED PROCEDURES
   ───────────────────────────────────────────────────────────────────────────── */

/* 2.1  Mark a conversation read up to "now" (server clock, so it is always at
        or after the SentAtUtc / UploadedAtUtc of everything already saved).
        Only ever moves forward. Caller must be an Accepted participant.
        RETURN 0 = saved, 1 = not an accepted participant.                      */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_MarkConversationRead
    @ConversationId  NVARCHAR(450),
    @UserId          NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1
                   FROM   dbo.DoctorCommunications_ConversationParticipants
                   WHERE  ConversationId = @ConversationId
                     AND  UserId         = @UserId
                     AND  Status         = N'Accepted')
        RETURN 1;

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

        UPDATE dbo.DoctorCommunications_ReadState WITH (UPDLOCK, SERIALIZABLE)
        SET    LastReadAtUtc = CASE WHEN LastReadAtUtc > @Now THEN LastReadAtUtc ELSE @Now END
        WHERE  UserId         = @UserId
          AND  ConversationId = @ConversationId;

        IF @@ROWCOUNT = 0
            INSERT INTO dbo.DoctorCommunications_ReadState (ConversationId, UserId, LastReadAtUtc)
            VALUES (@ConversationId, @UserId, @Now);

    COMMIT TRANSACTION;

    RETURN 0;
END
GO

/* 2.2  Unread counts for every conversation the user has Accepted.
        Unread = messages + shared documents from OTHER people newer than the
        user's LastReadAtUtc (everything, when the user has never read it).
        Result set: ConversationId, UnreadCount                                  */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_GetUnreadCounts
    @UserId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.ConversationId,
           ISNULL(msg.Cnt, 0) + ISNULL(doc.Cnt, 0) AS UnreadCount
    FROM   dbo.DoctorCommunications_ConversationParticipants p
    LEFT JOIN dbo.DoctorCommunications_ReadState r
           ON r.UserId         = p.UserId
          AND r.ConversationId = p.ConversationId
    OUTER APPLY (SELECT COUNT(*) AS Cnt
                 FROM   dbo.DoctorCommunications_ChatMessages m
                 WHERE  m.ConversationId = p.ConversationId
                   AND  m.SenderUserId  <> @UserId
                   AND  (r.LastReadAtUtc IS NULL OR m.SentAtUtc > r.LastReadAtUtc)) msg
    OUTER APPLY (SELECT COUNT(*) AS Cnt
                 FROM   dbo.DoctorCommunications_SharedDocuments d
                 WHERE  d.ConversationId  = p.ConversationId
                   AND  d.UploaderUserId <> @UserId
                   AND  (r.LastReadAtUtc IS NULL OR d.UploadedAtUtc > r.LastReadAtUtc)) doc
    WHERE  p.UserId = @UserId
      AND  p.Status = N'Accepted';
END
GO

/* ─────────────────────────────────────────────────────────────────────────────
   3. OPTIONAL ONE-TIME SEED (run once, at go-live only)
   ----------------------------------------------------------------------------
   Without it, every existing conversation has no read state yet, so the first
   login after go-live shows ALL past messages from others as unread. This
   marks everything that exists right now as read for every accepted
   participant. Do NOT re-run later — it would silently mark newer, genuinely
   unread conversations as read.
   ───────────────────────────────────────────────────────────────────────────── */

-- INSERT INTO dbo.DoctorCommunications_ReadState (ConversationId, UserId, LastReadAtUtc)
-- SELECT p.ConversationId, p.UserId, SYSUTCDATETIME()
-- FROM   dbo.DoctorCommunications_ConversationParticipants p
-- WHERE  p.Status = N'Accepted'
--   AND  NOT EXISTS (SELECT 1 FROM dbo.DoctorCommunications_ReadState r
--                    WHERE r.UserId = p.UserId AND r.ConversationId = p.ConversationId);
-- GO
