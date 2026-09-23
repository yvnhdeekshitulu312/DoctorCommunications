/* ════════════════════════════════════════════════════════════════════════════
   DoctorCommunications — Doctor-to-doctor chat
   Tables + Table type + Stored procedures
   ----------------------------------------------------------------------------
   • Replaces the EF Core entities / DbContext (ChatModels.cs,
     DoctorCommunicationsDbContext.cs).
   • Re-runnable: tables / indexes / type are created only if missing,
     procedures use CREATE OR ALTER (SQL Server 2016 SP1+).
   • Table + index names match what EF Core generated, so an existing
     EF-created database is picked up as-is (no data migration needed).
   • All timestamps are UTC (DATETIME2(3)).
   ════════════════════════════════════════════════════════════════════════════ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ─────────────────────────────────────────────────────────────────────────────
   1. TABLES
   ───────────────────────────────────────────────────────────────────────────── */

IF OBJECT_ID(N'dbo.DoctorCommunications_Conversations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DoctorCommunications_Conversations
    (
        Id               NVARCHAR(450) NOT NULL,
        CreatedByUserId  NVARCHAR(450) NOT NULL,
        CreatedAtUtc     DATETIME2(3)  NOT NULL
            CONSTRAINT DF_DoctorCommunications_Conversations_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_DoctorCommunications_Conversations PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF OBJECT_ID(N'dbo.DoctorCommunications_ConversationParticipants', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DoctorCommunications_ConversationParticipants
    (
        Id              INT IDENTITY(1,1) NOT NULL,
        ConversationId  NVARCHAR(450)     NOT NULL,
        UserId          NVARCHAR(450)     NOT NULL,
        Name            NVARCHAR(200)     NOT NULL,
        Status          NVARCHAR(20)      NOT NULL,   -- Pending | Accepted | Declined
        RespondedAtUtc  DATETIME2(3)      NULL,

        CONSTRAINT PK_DoctorCommunications_ConversationParticipants PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_DoctorCommunications_ConversationParticipants_DoctorCommunications_Conversations_ConversationId
            FOREIGN KEY (ConversationId) REFERENCES dbo.DoctorCommunications_Conversations (Id) ON DELETE CASCADE,
        CONSTRAINT CK_DoctorCommunications_ConversationParticipants_Status
            CHECK (Status IN (N'Pending', N'Accepted', N'Declined'))
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_DoctorCommunications_ConversationParticipants_ConversationId_UserId'
                 AND object_id = OBJECT_ID(N'dbo.DoctorCommunications_ConversationParticipants'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_DoctorCommunications_ConversationParticipants_ConversationId_UserId
        ON dbo.DoctorCommunications_ConversationParticipants (ConversationId, UserId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_DoctorCommunications_ConversationParticipants_UserId_Status'
                 AND object_id = OBJECT_ID(N'dbo.DoctorCommunications_ConversationParticipants'))
    CREATE NONCLUSTERED INDEX IX_DoctorCommunications_ConversationParticipants_UserId_Status
        ON dbo.DoctorCommunications_ConversationParticipants (UserId, Status)
        INCLUDE (ConversationId);
GO

IF OBJECT_ID(N'dbo.DoctorCommunications_ChatMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DoctorCommunications_ChatMessages
    (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        ConversationId  NVARCHAR(450)        NOT NULL,
        SenderUserId    NVARCHAR(450)        NOT NULL,
        SenderName      NVARCHAR(200)        NOT NULL,
        Text            NVARCHAR(MAX)        NOT NULL,
        SentAtUtc       DATETIME2(3)         NOT NULL
            CONSTRAINT DF_DoctorCommunications_ChatMessages_SentAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_DoctorCommunications_ChatMessages PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_DoctorCommunications_ChatMessages_DoctorCommunications_Conversations_ConversationId
            FOREIGN KEY (ConversationId) REFERENCES dbo.DoctorCommunications_Conversations (Id) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_DoctorCommunications_ChatMessages_ConversationId_SentAtUtc'
                 AND object_id = OBJECT_ID(N'dbo.DoctorCommunications_ChatMessages'))
    CREATE NONCLUSTERED INDEX IX_DoctorCommunications_ChatMessages_ConversationId_SentAtUtc
        ON dbo.DoctorCommunications_ChatMessages (ConversationId, SentAtUtc);
GO

/* ─────────────────────────────────────────────────────────────────────────────
   2. TABLE TYPE — invitee list for PR_DoctorComm_CreateConversation
   ───────────────────────────────────────────────────────────────────────────── */

IF TYPE_ID(N'dbo.DoctorCommunications_ParticipantList') IS NULL
    CREATE TYPE dbo.DoctorCommunications_ParticipantList AS TABLE
    (
        UserId NVARCHAR(450) NOT NULL,
        Name   NVARCHAR(200) NOT NULL
    );
GO

/* ─────────────────────────────────────────────────────────────────────────────
   3. STORED PROCEDURES
   ───────────────────────────────────────────────────────────────────────────── */

/* 3.1  Create a conversation.
        Starter is inserted as Accepted, every invitee as Pending.
        Returns: @ConversationId (OUTPUT) + result set of all participants.      */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_CreateConversation
    @ConversationId   NVARCHAR(450) = NULL OUTPUT,
    @CreatedByUserId  NVARCHAR(450),
    @CreatedByName    NVARCHAR(200),
    @Invitees         dbo.DoctorCommunications_ParticipantList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @ConversationId IS NULL OR LTRIM(RTRIM(@ConversationId)) = N''
        SET @ConversationId = LOWER(CONVERT(NVARCHAR(36), NEWID()));

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

        INSERT INTO dbo.DoctorCommunications_Conversations (Id, CreatedByUserId, CreatedAtUtc)
        VALUES (@ConversationId, @CreatedByUserId, @Now);

        INSERT INTO dbo.DoctorCommunications_ConversationParticipants
            (ConversationId, UserId, Name, Status, RespondedAtUtc)
        VALUES
            (@ConversationId, @CreatedByUserId, @CreatedByName, N'Accepted', @Now);

        -- De-duplicate invitees and never re-insert the starter
        INSERT INTO dbo.DoctorCommunications_ConversationParticipants
            (ConversationId, UserId, Name, Status, RespondedAtUtc)
        SELECT @ConversationId, i.UserId, MAX(i.Name), N'Pending', NULL
        FROM   @Invitees i
        WHERE  i.UserId <> @CreatedByUserId
          AND  LTRIM(RTRIM(i.UserId)) <> N''
        GROUP BY i.UserId;

    COMMIT TRANSACTION;

    SELECT p.UserId, p.Name, p.Status
    FROM   dbo.DoctorCommunications_ConversationParticipants p
    WHERE  p.ConversationId = @ConversationId
    ORDER BY p.Id;
END
GO

/* 3.2  Accept / decline an invite (only while it is still Pending).
        Returns: @Updated (OUTPUT, 1 = status changed) + all participants.       */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_RespondToInvite
    @ConversationId  NVARCHAR(450),
    @UserId          NVARCHAR(450),
    @Accept          BIT,
    @Updated         BIT = 0 OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.DoctorCommunications_ConversationParticipants
    SET    Status         = CASE WHEN @Accept = 1 THEN N'Accepted' ELSE N'Declined' END,
           RespondedAtUtc = SYSUTCDATETIME()
    WHERE  ConversationId = @ConversationId
      AND  UserId         = @UserId
      AND  Status         = N'Pending';

    SET @Updated = CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END;

    SELECT p.UserId, p.Name, p.Status
    FROM   dbo.DoctorCommunications_ConversationParticipants p
    WHERE  p.ConversationId = @ConversationId
    ORDER BY p.Id;
END
GO

/* 3.3  Save a chat message (sender must be an Accepted participant).
        RETURN 0 = saved, 1 = sender not an accepted participant, 2 = empty text.
        Returns: @MessageId, @SentAtUtc (OUTPUT).                                */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_SaveMessage
    @ConversationId  NVARCHAR(450),
    @SenderUserId    NVARCHAR(450),
    @SenderName      NVARCHAR(200),
    @Text            NVARCHAR(MAX),
    @MessageId       BIGINT       = NULL OUTPUT,
    @SentAtUtc       DATETIME2(3) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @Text IS NULL OR LTRIM(RTRIM(@Text)) = N''
        RETURN 2;

    IF NOT EXISTS (SELECT 1
                   FROM   dbo.DoctorCommunications_ConversationParticipants
                   WHERE  ConversationId = @ConversationId
                     AND  UserId         = @SenderUserId
                     AND  Status         = N'Accepted')
        RETURN 1;

    SET @SentAtUtc = SYSUTCDATETIME();

    INSERT INTO dbo.DoctorCommunications_ChatMessages
        (ConversationId, SenderUserId, SenderName, Text, SentAtUtc)
    VALUES
        (@ConversationId, @SenderUserId, @SenderName, @Text, @SentAtUtc);

    SET @MessageId = SCOPE_IDENTITY();
    RETURN 0;
END
GO

/* 3.4  Pending invites for a user  (GET /api/conversations/pending/{userId})
        Result set 1: invites   | Result set 2: participants of those invites    */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_GetPendingInvites
    @UserId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Conv TABLE (ConversationId NVARCHAR(450) PRIMARY KEY);

    INSERT INTO @Conv (ConversationId)
    SELECT DISTINCT p.ConversationId
    FROM   dbo.DoctorCommunications_ConversationParticipants p
    WHERE  p.UserId = @UserId
      AND  p.Status = N'Pending';

    -- 1) invites
    SELECT c.Id               AS ConversationId,
           c.CreatedByUserId  AS StarterUserId,
           starter.Name       AS StarterName,
           c.CreatedAtUtc
    FROM   @Conv x
    JOIN   dbo.DoctorCommunications_Conversations c
           ON c.Id = x.ConversationId
    OUTER APPLY (SELECT TOP (1) s.Name
                 FROM   dbo.DoctorCommunications_ConversationParticipants s
                 WHERE  s.ConversationId = c.Id
                   AND  s.UserId         = c.CreatedByUserId) starter
    ORDER BY c.CreatedAtUtc DESC;

    -- 2) all participants of those conversations
    SELECT p.ConversationId, p.UserId, p.Name
    FROM   @Conv x
    JOIN   dbo.DoctorCommunications_ConversationParticipants p
           ON p.ConversationId = x.ConversationId
    ORDER BY p.ConversationId, p.Id;
END
GO

/* 3.5  A user's accepted conversations  (GET /api/conversations/mine/{userId})
        Result set 1: conversations (+ last message, newest activity first)
        Result set 2: accepted participants of those conversations               */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_GetMyConversations
    @UserId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Conv TABLE (ConversationId NVARCHAR(450) PRIMARY KEY);

    INSERT INTO @Conv (ConversationId)
    SELECT DISTINCT p.ConversationId
    FROM   dbo.DoctorCommunications_ConversationParticipants p
    WHERE  p.UserId = @UserId
      AND  p.Status = N'Accepted';

    -- 1) conversations
    SELECT c.Id            AS ConversationId,
           c.CreatedAtUtc,
           lastMsg.SentAtUtc AS LastMessageAtUtc,
           lastMsg.Text      AS LastMessageText
    FROM   @Conv x
    JOIN   dbo.DoctorCommunications_Conversations c
           ON c.Id = x.ConversationId
    OUTER APPLY (SELECT TOP (1) m.SentAtUtc, m.Text
                 FROM   dbo.DoctorCommunications_ChatMessages m
                 WHERE  m.ConversationId = c.Id
                 ORDER BY m.SentAtUtc DESC, m.Id DESC) lastMsg
    ORDER BY COALESCE(lastMsg.SentAtUtc, c.CreatedAtUtc) DESC;

    -- 2) accepted participants
    SELECT p.ConversationId, p.UserId, p.Name
    FROM   @Conv x
    JOIN   dbo.DoctorCommunications_ConversationParticipants p
           ON p.ConversationId = x.ConversationId
    WHERE  p.Status = N'Accepted'
    ORDER BY p.ConversationId, p.Id;
END
GO

/* 3.6  Message history  (GET /api/conversations/{id}/messages?callerUserId=)
        @IsParticipant (OUTPUT) = 0 → caller is not an accepted participant,
        no rows are returned (API responds 403).                                 */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_GetConversationMessages
    @ConversationId  NVARCHAR(450),
    @CallerUserId    NVARCHAR(450),
    @IsParticipant   BIT = 0 OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @IsParticipant =
        CASE WHEN EXISTS (SELECT 1
                          FROM   dbo.DoctorCommunications_ConversationParticipants
                          WHERE  ConversationId = @ConversationId
                            AND  UserId         = @CallerUserId
                            AND  Status         = N'Accepted')
             THEN 1 ELSE 0 END;

    SELECT m.Id,
           m.SenderUserId,
           m.SenderName,
           m.Text,
           m.SentAtUtc
    FROM   dbo.DoctorCommunications_ChatMessages m
    WHERE  @IsParticipant  = 1
      AND  m.ConversationId = @ConversationId
    ORDER BY m.SentAtUtc, m.Id;
END
GO

/* 3.7  All participants (any status) — used by the SignalR hub to fan out
        invites / messages / join notifications.                                 */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_GetConversationParticipants
    @ConversationId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.UserId, p.Name, p.Status, p.RespondedAtUtc
    FROM   dbo.DoctorCommunications_ConversationParticipants p
    WHERE  p.ConversationId = @ConversationId
    ORDER BY p.Id;
END
GO

/* 3.8  Is this user an Accepted participant? (hub guard for JoinGroup etc.)   */
CREATE OR ALTER PROCEDURE dbo.PR_DoctorComm_IsAcceptedParticipant
    @ConversationId NVARCHAR(450),
    @UserId         NVARCHAR(450),
    @IsParticipant  BIT = 0 OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @IsParticipant =
        CASE WHEN EXISTS (SELECT 1
                          FROM   dbo.DoctorCommunications_ConversationParticipants
                          WHERE  ConversationId = @ConversationId
                            AND  UserId         = @UserId
                            AND  Status         = N'Accepted')
             THEN 1 ELSE 0 END;
END
GO
