using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class AddClubAccessKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessKey",
                table: "Clubs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_AccessKey",
                table: "Clubs",
                column: "AccessKey",
                unique: true,
                filter: "\"AccessKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clubs_AccessKey",
                table: "Clubs");

            migrationBuilder.DropColumn(
                name: "AccessKey",
                table: "Clubs");
        }
    }
}
