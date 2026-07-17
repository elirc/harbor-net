using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sprint12EmailChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Inboxes_WorkspaceId",
                table: "Inboxes");

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmailMessageId",
                table: "Messages",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailAddress",
                table: "Inboxes",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_EmailMessageId",
                table: "Messages",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Inboxes_WorkspaceId_EmailAddress",
                table: "Inboxes",
                columns: new[] { "WorkspaceId", "EmailAddress" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_EmailMessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Inboxes_WorkspaceId_EmailAddress",
                table: "Inboxes");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "EmailMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "EmailAddress",
                table: "Inboxes");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Inboxes_WorkspaceId",
                table: "Inboxes",
                column: "WorkspaceId");
        }
    }
}
