using System;
using VenuePlus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    public partial class AddClubs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clubs",
                columns: table => new
                {
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clubs", x => x.ClubId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_ClubId",
                table: "Clubs",
                column: "ClubId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Clubs");
        }
    }
}
