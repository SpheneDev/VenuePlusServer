using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAdminPinAndPubKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminPins");

            migrationBuilder.DropTable(
                name: "AllowedAdminPubKeys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminPins",
                columns: table => new
                {
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PinHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminPins", x => x.ClubId);
                });

            migrationBuilder.CreateTable(
                name: "AllowedAdminPubKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PubKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedAdminPubKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllowedAdminPubKeys_ClubId_PubKey",
                table: "AllowedAdminPubKeys",
                columns: new[] { "ClubId", "PubKey" },
                unique: true);
        }
    }
}
