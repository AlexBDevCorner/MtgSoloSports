using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddAthleteProfileIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoryEvents_SaveAthleteId",
                table: "StoryEvents");

            migrationBuilder.CreateIndex(
                name: "IX_StoryEvents_SaveAthleteId_Id",
                table: "StoryEvents",
                columns: new[] { "SaveAthleteId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_SaveAthleteId_ToSeasonId_Kind",
                table: "Movements",
                columns: new[] { "SaveAthleteId", "ToSeasonId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoryEvents_SaveAthleteId_Id",
                table: "StoryEvents");

            migrationBuilder.DropIndex(
                name: "IX_Movements_SaveAthleteId_ToSeasonId_Kind",
                table: "Movements");

            migrationBuilder.CreateIndex(
                name: "IX_StoryEvents_SaveAthleteId",
                table: "StoryEvents",
                column: "SaveAthleteId");
        }
    }
}
