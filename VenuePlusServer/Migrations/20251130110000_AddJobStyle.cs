using Microsoft.EntityFrameworkCore.Migrations;

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    public partial class AddJobStyle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ColorHex",
                table: "JobRights",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "#FFFFFF");

            migrationBuilder.AddColumn<string>(
                name: "IconKey",
                table: "JobRights",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "User");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColorHex",
                table: "JobRights");

            migrationBuilder.DropColumn(
                name: "IconKey",
                table: "JobRights");
        }
    }
}
