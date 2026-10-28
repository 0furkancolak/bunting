using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bunting.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyHint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hint",
                table: "api_keys",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hint",
                table: "api_keys");
        }
    }
}
