using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sprint11Webhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookSubscriptions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", maxLength: 100000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DeliveredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_WebhookSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "WebhookSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookSubscriptionEvent",
                columns: table => new
                {
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSubscriptionEvent", x => new { x.SubscriptionId, x.EventType });
                    table.ForeignKey(
                        name: "FK_WebhookSubscriptionEvent_WebhookSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "WebhookSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_SubscriptionId",
                table: "WebhookDeliveries",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WorkspaceId_Status_NextAttemptAt",
                table: "WebhookDeliveries",
                columns: new[] { "WorkspaceId", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_WorkspaceId",
                table: "WebhookSubscriptions",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "WebhookSubscriptionEvent");

            migrationBuilder.DropTable(
                name: "WebhookSubscriptions");
        }
    }
}
