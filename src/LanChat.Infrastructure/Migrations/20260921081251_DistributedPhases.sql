ALTER TABLE "PeerNodes" ADD COLUMN "ApplicationVersion" TEXT NOT NULL DEFAULT '';

ALTER TABLE "Messages" ADD COLUMN "ForwardedFromMessageId" TEXT;

ALTER TABLE "Messages" ADD COLUMN "IsDeletedLocal" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Messages" ADD COLUMN "IsEdited" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Messages" ADD COLUMN "IsRecalled" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Messages" ADD COLUMN "ReplyToMessageId" TEXT;

ALTER TABLE "LocalSettings" ADD COLUMN "ClusterId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE "LocalSettings" ADD COLUMN "EventSequence" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Conversations" ADD COLUMN "Title" TEXT NOT NULL DEFAULT '';

CREATE TABLE "Attachments" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "MessageId" TEXT NOT NULL,
    "FileName" TEXT NOT NULL,
    "MimeType" TEXT NOT NULL,
    "FileSize" INTEGER NOT NULL,
    "Sha256" TEXT NOT NULL
);

CREATE TABLE "ClusterState" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "ClusterId" TEXT NOT NULL,
    "CurrentTerm" INTEGER NOT NULL,
    "VotedForNodeId" TEXT,
    "CurrentLeaderNodeId" TEXT,
    "CommitIndex" INTEGER NOT NULL,
    "LastAppliedIndex" INTEGER NOT NULL
);

CREATE TABLE "ControlLog" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "Index" INTEGER NOT NULL,
    "Term" INTEGER NOT NULL,
    "CommandJson" TEXT NOT NULL
);

CREATE TABLE "GroupMembers" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "GroupId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL
);

CREATE TABLE "Groups" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "ConversationId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "OwnerUserId" TEXT NOT NULL,
    "AvatarBase64" TEXT,
    "IsDeleted" INTEGER NOT NULL
);

CREATE TABLE "MessageReactions" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "MessageId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Emoji" TEXT NOT NULL
);

CREATE TABLE "MessageReceipts" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "MessageId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "IsRead" INTEGER NOT NULL,
    "ReceivedAtTicks" INTEGER NOT NULL
);

CREATE TABLE "OutboxItems" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "ReplicationEventId" TEXT NOT NULL,
    "Status" INTEGER NOT NULL,
    "RetryCount" INTEGER NOT NULL,
    "NextRetryTicks" INTEGER NOT NULL,
    "LastError" TEXT
);

CREATE TABLE "ReplicationCursors" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "OriginNodeId" TEXT NOT NULL,
    "LastOriginSequence" INTEGER NOT NULL
);

CREATE TABLE "ReplicationEvents" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "OriginNodeId" TEXT NOT NULL,
    "OriginSequence" INTEGER NOT NULL,
    "ActorUserId" TEXT NOT NULL,
    "EntityId" TEXT NOT NULL,
    "ConversationId" TEXT,
    "Type" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PhysicalTicks" INTEGER NOT NULL,
    "LogicalCounter" INTEGER NOT NULL,
    "CreatedAtTicks" INTEGER NOT NULL,
    "Signature" TEXT NOT NULL
);

CREATE INDEX "IX_Attachments_MessageId" ON "Attachments" ("MessageId");

CREATE INDEX "IX_Attachments_Sha256" ON "Attachments" ("Sha256");

CREATE UNIQUE INDEX "IX_ControlLog_Index" ON "ControlLog" ("Index");

CREATE UNIQUE INDEX "IX_GroupMembers_GroupId_UserId" ON "GroupMembers" ("GroupId", "UserId");

CREATE UNIQUE INDEX "IX_MessageReactions_MessageId_UserId_Emoji" ON "MessageReactions" ("MessageId", "UserId", "Emoji");

CREATE UNIQUE INDEX "IX_MessageReceipts_MessageId_UserId" ON "MessageReceipts" ("MessageId", "UserId");

CREATE UNIQUE INDEX "IX_OutboxItems_ReplicationEventId" ON "OutboxItems" ("ReplicationEventId");

CREATE INDEX "IX_OutboxItems_Status_NextRetryTicks" ON "OutboxItems" ("Status", "NextRetryTicks");

CREATE UNIQUE INDEX "IX_ReplicationCursors_OriginNodeId" ON "ReplicationCursors" ("OriginNodeId");

CREATE INDEX "IX_ReplicationEvents_EntityId_PhysicalTicks_LogicalCounter" ON "ReplicationEvents" ("EntityId", "PhysicalTicks", "LogicalCounter");

CREATE UNIQUE INDEX "IX_ReplicationEvents_OriginNodeId_OriginSequence" ON "ReplicationEvents" ("OriginNodeId", "OriginSequence");

CREATE VIRTUAL TABLE MessageSearch USING fts5(Id UNINDEXED, ConversationId UNINDEXED, Text, tokenize='unicode61 remove_diacritics 2');
INSERT INTO MessageSearch(Id,ConversationId,Text) SELECT Id,ConversationId,Text FROM Messages;
CREATE TRIGGER message_search_insert AFTER INSERT ON Messages BEGIN
    INSERT INTO MessageSearch(Id,ConversationId,Text) VALUES(new.Id,new.ConversationId,new.Text);
END;
CREATE TRIGGER message_search_update AFTER UPDATE OF Text ON Messages BEGIN
    DELETE FROM MessageSearch WHERE Id=old.Id;
    INSERT INTO MessageSearch(Id,ConversationId,Text) VALUES(new.Id,new.ConversationId,new.Text);
END;
CREATE TRIGGER message_search_delete AFTER DELETE ON Messages BEGIN DELETE FROM MessageSearch WHERE Id=old.Id; END;
CREATE TRIGGER events_immutable_update BEFORE UPDATE ON ReplicationEvents BEGIN SELECT RAISE(ABORT,'Replication events are immutable'); END;
CREATE TRIGGER events_immutable_delete BEFORE DELETE ON ReplicationEvents BEGIN SELECT RAISE(ABORT,'Replication events are immutable'); END;
