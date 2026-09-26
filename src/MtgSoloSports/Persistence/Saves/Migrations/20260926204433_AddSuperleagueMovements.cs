using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperleagueMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Movements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromLeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToLeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    FromSeasonRank = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_FromSeasonId_FromLeagueId",
                table: "Movements",
                columns: new[] { "FromSeasonId", "FromLeagueId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ToSeasonId_SaveAthleteId",
                table: "Movements",
                columns: new[] { "ToSeasonId", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ToSeasonId_ToLeagueId",
                table: "Movements",
                columns: new[] { "ToSeasonId", "ToLeagueId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Movements");
        }
    }
}
