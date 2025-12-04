using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEditVipDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EditVipDuration",
                table: "JobRights",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditVipDuration",
                table: "JobRights");
        }
    }
}
