using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LanChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DistributedPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicationVersion",
                table: "PeerNodes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ForwardedFromMessageId",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeletedLocal",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsEdited",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRecalled",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToMessageId",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClusterId",
                table: "LocalSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "EventSequence",
                table: "LocalSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClusterState",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrentTerm = table.Column<long>(type: "INTEGER", nullable: false),
                    VotedForNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CurrentLeaderNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CommitIndex = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAppliedIndex = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ControlLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<long>(type: "INTEGER", nullable: false),
                    Term = table.Column<long>(type: "INTEGER", nullable: false),
                    CommandJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AvatarBase64 = table.Column<string>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Emoji = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageReactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReplicationEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRetryTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReplicationCursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastOriginSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplicationCursors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReplicationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LogicalCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplicationEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_MessageId",
                table: "Attachments",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_Sha256",
                table: "Attachments",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_ControlLog_Index",
                table: "ControlLog",
                column: "Index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_GroupId_UserId",
                table: "GroupMembers",
                columns: new[] { "GroupId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageReactions_MessageId_UserId_Emoji",
                table: "MessageReactions",
                columns: new[] { "MessageId", "UserId", "Emoji" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageReceipts_MessageId_UserId",
                table: "MessageReceipts",
                columns: new[] { "MessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxItems_ReplicationEventId",
                table: "OutboxItems",
                column: "ReplicationEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxItems_Status_NextRetryTicks",
                table: "OutboxItems",
                columns: new[] { "Status", "NextRetryTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplicationCursors_OriginNodeId",
                table: "ReplicationCursors",
                column: "OriginNodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReplicationEvents_EntityId_PhysicalTicks_LogicalCounter",
                table: "ReplicationEvents",
                columns: new[] { "EntityId", "PhysicalTicks", "LogicalCounter" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplicationEvents_OriginNodeId_OriginSequence",
                table: "ReplicationEvents",
                columns: new[] { "OriginNodeId", "OriginSequence" },
                unique: true);
            migrationBuilder.Sql("""
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS message_search_insert; DROP TRIGGER IF EXISTS message_search_update; DROP TRIGGER IF EXISTS message_search_delete; DROP TABLE IF EXISTS MessageSearch; DROP TRIGGER IF EXISTS events_immutable_update; DROP TRIGGER IF EXISTS events_immutable_delete;");
            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "ClusterState");

            migrationBuilder.DropTable(
                name: "ControlLog");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "MessageReactions");

            migrationBuilder.DropTable(
                name: "MessageReceipts");

            migrationBuilder.DropTable(
                name: "OutboxItems");

            migrationBuilder.DropTable(
                name: "ReplicationCursors");

            migrationBuilder.DropTable(
                name: "ReplicationEvents");

            migrationBuilder.DropColumn(
                name: "ApplicationVersion",
                table: "PeerNodes");

            migrationBuilder.DropColumn(
                name: "ForwardedFromMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsDeletedLocal",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsEdited",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsRecalled",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReplyToMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                table: "LocalSettings");

            migrationBuilder.DropColumn(
                name: "EventSequence",
                table: "LocalSettings");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Conversations");
        }
    }
}
