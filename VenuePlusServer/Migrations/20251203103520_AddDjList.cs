using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDjList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DjEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DjName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TwitchLink = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DjEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DjEntries_ClubId_DjName",
                table: "DjEntries",
                columns: new[] { "ClubId", "DjName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DjEntries");
        }
    }
}
