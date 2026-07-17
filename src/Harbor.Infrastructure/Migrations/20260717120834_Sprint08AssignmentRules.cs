using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sprint08AssignmentRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Availability",
                table: "Teammates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CapacityLimit",
                table: "Teammates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoAssign",
                table: "Inboxes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "LastAssignedTeammateId",
                table: "Inboxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssignmentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorTeammateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FromTeammateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FromTeamId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToTeammateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToTeamId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentEvents_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentEvents_ConversationId_CreatedAt",
                table: "AssignmentEvents",
                columns: new[] { "ConversationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentEvents");

            migrationBuilder.DropColumn(
                name: "Availability",
                table: "Teammates");

            migrationBuilder.DropColumn(
                name: "CapacityLimit",
                table: "Teammates");

            migrationBuilder.DropColumn(
                name: "AutoAssign",
                table: "Inboxes");

            migrationBuilder.DropColumn(
                name: "LastAssignedTeammateId",
                table: "Inboxes");
        }
    }
}
