using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class AddClubJoinPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JoinPasswordHash",
                table: "Clubs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinPasswordHash",
                table: "Clubs");
        }
    }
}
