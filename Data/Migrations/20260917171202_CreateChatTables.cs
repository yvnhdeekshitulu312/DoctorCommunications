using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoctorCommunications.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateChatTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoctorCommunications_Conversations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorCommunications_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DoctorCommunications_ChatMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConversationId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SenderUserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SenderName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorCommunications_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoctorCommunications_ChatMessages_DoctorCommunications_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "DoctorCommunications_Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DoctorCommunications_ConversationParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConversationId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorCommunications_ConversationParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoctorCommunications_ConversationParticipants_DoctorCommunications_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "DoctorCommunications_Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorCommunications_ChatMessages_ConversationId_SentAtUtc",
                table: "DoctorCommunications_ChatMessages",
                columns: new[] { "ConversationId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorCommunications_ConversationParticipants_ConversationId_UserId",
                table: "DoctorCommunications_ConversationParticipants",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorCommunications_ConversationParticipants_UserId_Status",
                table: "DoctorCommunications_ConversationParticipants",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoctorCommunications_ChatMessages");

            migrationBuilder.DropTable(
                name: "DoctorCommunications_ConversationParticipants");

            migrationBuilder.DropTable(
                name: "DoctorCommunications_Conversations");
        }
    }
}
