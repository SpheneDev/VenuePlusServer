using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseUsersAndMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BaseUsers",
                columns: table => new
                {
                    Uid = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaseUsers", x => x.Uid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BaseUsers_Username",
                table: "BaseUsers",
                column: "Username",
                unique: true);

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_StaffUsers_ClubId_Username\" ON \"StaffUsers\" (\"ClubId\", \"Username\")");

            migrationBuilder.AddColumn<string>(
                name: "UserUid",
                table: "StaffUsers",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.Sql("UPDATE \"StaffUsers\" SET \"UserUid\" = LEFT(regexp_replace(UPPER(\"Username\"), '[^A-Z0-9]', '', 'g'), 15)");

            migrationBuilder.Sql("INSERT INTO \"BaseUsers\" (\"Uid\", \"Username\", \"PasswordHash\", \"CreatedAt\") SELECT LEFT(regexp_replace(UPPER(\"Username\"), '[^A-Z0-9]', '', 'g'), 15), \"Username\", \"PasswordHash\", COALESCE(\"CreatedAt\", NOW()) FROM \"StaffUsers\" ON CONFLICT (\"Username\") DO NOTHING");

            migrationBuilder.AlterColumn<string>(
                name: "UserUid",
                table: "StaffUsers",
                type: "character varying(15)",
                maxLength: 15,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(15)",
                oldMaxLength: 15,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffUsers_ClubId_UserUid",
                table: "StaffUsers",
                columns: new[] { "ClubId", "UserUid" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_StaffUsers_ClubId_Username",
                table: "StaffUsers");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "StaffUsers");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "StaffUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BaseUsers");

            migrationBuilder.DropIndex(
                name: "IX_StaffUsers_ClubId_UserUid",
                table: "StaffUsers");

            migrationBuilder.DropColumn(
                name: "UserUid",
                table: "StaffUsers");

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "StaffUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "StaffUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_StaffUsers_ClubId_Username",
                table: "StaffUsers",
                columns: new[] { "ClubId", "Username" },
                unique: true);
        }
    }
}
