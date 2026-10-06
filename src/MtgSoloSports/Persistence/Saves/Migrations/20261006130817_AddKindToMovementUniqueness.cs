using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddKindToMovementUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Movements_ToSeasonId_SaveAthleteId",
                table: "Movements");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ToSeasonId_SaveAthleteId_Kind",
                table: "Movements",
                columns: new[] { "ToSeasonId", "SaveAthleteId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Movements_ToSeasonId_SaveAthleteId_Kind",
                table: "Movements");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ToSeasonId_SaveAthleteId",
                table: "Movements",
                columns: new[] { "ToSeasonId", "SaveAthleteId" },
                unique: true);
        }
    }
}
