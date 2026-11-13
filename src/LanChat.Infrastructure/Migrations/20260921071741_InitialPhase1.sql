CREATE TABLE "ConversationMembers" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "ConversationId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL
);

CREATE TABLE "Conversations" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "Kind" INTEGER NOT NULL,
    "IsPinned" INTEGER NOT NULL,
    "IsMuted" INTEGER NOT NULL,
    "LastReadTicks" INTEGER NOT NULL
);

CREATE TABLE "LocalSettings" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "NodeId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "CreatedAtTicks" INTEGER NOT NULL,
    "LastSequence" INTEGER NOT NULL,
    "PhysicalTicks" INTEGER NOT NULL,
    "LogicalCounter" INTEGER NOT NULL
);

CREATE TABLE "Messages" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "ClientMessageId" TEXT NOT NULL,
    "ConversationId" TEXT NOT NULL,
    "SenderUserId" TEXT NOT NULL,
    "RecipientUserId" TEXT NOT NULL,
    "OriginNodeId" TEXT NOT NULL,
    "OriginSequence" INTEGER NOT NULL,
    "Kind" INTEGER NOT NULL,
    "Text" TEXT NOT NULL,
    "CreatedAtTicks" INTEGER NOT NULL,
    "PhysicalTicks" INTEGER NOT NULL,
    "LogicalCounter" INTEGER NOT NULL,
    "Status" INTEGER NOT NULL,
    "RetryCount" INTEGER NOT NULL,
    "NextRetryTicks" INTEGER NOT NULL,
    "LastError" TEXT
);

CREATE TABLE "PeerNodes" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "NodeId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "DeviceName" TEXT NOT NULL,
    "IPAddress" TEXT NOT NULL,
    "ServicePort" INTEGER NOT NULL,
    "LastSeenTicks" INTEGER NOT NULL,
    "Role" INTEGER NOT NULL,
    "Term" INTEGER NOT NULL
);

CREATE TABLE "Users" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "DisplayName" TEXT NOT NULL,
    "FullName" TEXT,
    "Nickname" TEXT,
    "Department" TEXT,
    "Position" TEXT,
    "Phone" TEXT,
    "StatusMessage" TEXT,
    "AvatarPath" TEXT
);

CREATE UNIQUE INDEX "IX_ConversationMembers_ConversationId_UserId" ON "ConversationMembers" ("ConversationId", "UserId");

CREATE INDEX "IX_ConversationMembers_UserId" ON "ConversationMembers" ("UserId");

CREATE UNIQUE INDEX "IX_Messages_ClientMessageId" ON "Messages" ("ClientMessageId");

CREATE INDEX "IX_Messages_ConversationId_PhysicalTicks_LogicalCounter_OriginNodeId_Id" ON "Messages" ("ConversationId", "PhysicalTicks", "LogicalCounter", "OriginNodeId", "Id");

CREATE INDEX "IX_Messages_CreatedAtTicks" ON "Messages" ("CreatedAtTicks");

CREATE UNIQUE INDEX "IX_Messages_OriginNodeId_OriginSequence" ON "Messages" ("OriginNodeId", "OriginSequence");

CREATE INDEX "IX_Messages_Status_NextRetryTicks" ON "Messages" ("Status", "NextRetryTicks");

CREATE UNIQUE INDEX "IX_PeerNodes_NodeId" ON "PeerNodes" ("NodeId");
