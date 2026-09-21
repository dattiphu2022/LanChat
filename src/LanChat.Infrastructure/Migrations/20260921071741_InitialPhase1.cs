using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LanChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMuted = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastReadTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PhysicalTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LogicalCounter = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    PhysicalTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LogicalCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRetryTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    IPAddress = table.Column<string>(type: "TEXT", nullable: false),
                    ServicePort = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Term = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: true),
                    Nickname = table.Column<string>(type: "TEXT", nullable: true),
                    Department = table.Column<string>(type: "TEXT", nullable: true),
                    Position = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    StatusMessage = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_ConversationId_UserId",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_UserId",
                table: "ConversationMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ClientMessageId",
                table: "Messages",
                column: "ClientMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_PhysicalTicks_LogicalCounter_OriginNodeId_Id",
                table: "Messages",
                columns: new[] { "ConversationId", "PhysicalTicks", "LogicalCounter", "OriginNodeId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatedAtTicks",
                table: "Messages",
                column: "CreatedAtTicks");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OriginNodeId_OriginSequence",
                table: "Messages",
                columns: new[] { "OriginNodeId", "OriginSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Status_NextRetryTicks",
                table: "Messages",
                columns: new[] { "Status", "NextRetryTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_PeerNodes_NodeId",
                table: "PeerNodes",
                column: "NodeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMembers");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "LocalSettings");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "PeerNodes");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
