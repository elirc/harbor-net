using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sprint09SlaEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FirstResolvedAt",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ResolutionDueAt",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SlaPolicyId",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlaBreachEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BreachedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SlaPolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaBreachEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaBreachEvents_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    InboxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstResponseMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolutionMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Inboxes_InboxId",
                        column: x => x.InboxId,
                        principalTable: "Inboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SlaBreachEvents_ConversationId_Kind",
                table: "SlaBreachEvents",
                columns: new[] { "ConversationId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_InboxId",
                table: "SlaPolicies",
                column: "InboxId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_WorkspaceId_InboxId_Priority",
                table: "SlaPolicies",
                columns: new[] { "WorkspaceId", "InboxId", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlaBreachEvents");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropColumn(
                name: "FirstResolvedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ResolutionDueAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "SlaPolicyId",
                table: "Conversations");
        }
    }
}
