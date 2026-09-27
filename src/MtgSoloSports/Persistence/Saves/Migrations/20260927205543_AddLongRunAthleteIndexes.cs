using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddLongRunAthleteIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StageStandings_SaveAthleteId_SeasonId",
                table: "StageStandings",
                columns: new[] { "SaveAthleteId", "SeasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonStandings_SaveAthleteId",
                table: "SeasonStandings",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonMemberships_SaveAthleteId",
                table: "SeasonMemberships",
                column: "SaveAthleteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StageStandings_SaveAthleteId_SeasonId",
                table: "StageStandings");

            migrationBuilder.DropIndex(
                name: "IX_SeasonStandings_SaveAthleteId",
                table: "SeasonStandings");

            migrationBuilder.DropIndex(
                name: "IX_SeasonMemberships_SaveAthleteId",
                table: "SeasonMemberships");
        }
    }
}
