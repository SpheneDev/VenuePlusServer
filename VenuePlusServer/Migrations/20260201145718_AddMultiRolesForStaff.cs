using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VenuePlus.Server.VenuePlusServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiRolesForStaff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffUserJobs",
                columns: table => new
                {
                    ClubId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserUid = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    JobName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffUserJobs", x => new { x.ClubId, x.UserUid, x.JobName });
                });

            migrationBuilder.Sql("INSERT INTO \"StaffUserJobs\" (\"ClubId\", \"UserUid\", \"JobName\") SELECT \"ClubId\", \"UserUid\", \"Job\" FROM \"StaffUsers\" WHERE \"Job\" IS NOT NULL AND \"Job\" <> '' ON CONFLICT DO NOTHING");

            migrationBuilder.DropColumn(
                name: "Job",
                table: "StaffUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Job",
                table: "StaffUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE \"StaffUsers\" su SET \"Job\" = sj.\"JobName\" FROM (SELECT \"ClubId\", \"UserUid\", MIN(\"JobName\") AS \"JobName\" FROM \"StaffUserJobs\" GROUP BY \"ClubId\", \"UserUid\") sj WHERE su.\"ClubId\" = sj.\"ClubId\" AND su.\"UserUid\" = sj.\"UserUid\"");

            migrationBuilder.DropTable(
                name: "StaffUserJobs");
        }
    }
}
