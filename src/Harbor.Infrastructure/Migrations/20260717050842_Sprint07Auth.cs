using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sprint07Auth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "Teammates",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Teammates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Teammates_ApiKeyHash",
                table: "Teammates",
                column: "ApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Teammates_ApiKeyHash",
                table: "Teammates");

            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "Teammates");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Teammates");
        }
    }
}
