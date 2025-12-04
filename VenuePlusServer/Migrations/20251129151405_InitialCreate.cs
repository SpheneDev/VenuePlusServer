using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "JobRights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AddVip = table.Column<bool>(type: "boolean", nullable: false),
                    RemoveVip = table.Column<bool>(type: "boolean", nullable: false),
                    ManageUsers = table.Column<bool>(type: "boolean", nullable: false),
                    ManageJobs = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Job = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VipEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HomeWorld = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllowedAdminPubKeys_ClubId_PubKey",
                table: "AllowedAdminPubKeys",
                columns: new[] { "ClubId", "PubKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRights_ClubId_JobName",
                table: "JobRights",
                columns: new[] { "ClubId", "JobName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffUsers_ClubId_Username",
                table: "StaffUsers",
                columns: new[] { "ClubId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VipEntries_ClubId_CharacterName_HomeWorld",
                table: "VipEntries",
                columns: new[] { "ClubId", "CharacterName", "HomeWorld" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminPins");

            migrationBuilder.DropTable(
                name: "AllowedAdminPubKeys");

            migrationBuilder.DropTable(
                name: "JobRights");

            migrationBuilder.DropTable(
                name: "StaffUsers");

            migrationBuilder.DropTable(
                name: "VipEntries");
        }
    }
}
