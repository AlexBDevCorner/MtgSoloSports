using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasonStandings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsComplete",
                table: "Seasons",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SeasonStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonRank = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalChampionshipPointsThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalStageScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    StageWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    StagePlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsChampion = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonStandings_SeasonId_LeagueId",
                table: "SeasonStandings",
                columns: new[] { "SeasonId", "LeagueId" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonStandings_SeasonId_LeagueId_SaveAthleteId",
                table: "SeasonStandings",
                columns: new[] { "SeasonId", "LeagueId", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeasonStandings_SeasonId_LeagueId_SeasonRank",
                table: "SeasonStandings",
                columns: new[] { "SeasonId", "LeagueId", "SeasonRank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeasonStandings");

            migrationBuilder.DropColumn(
                name: "IsComplete",
                table: "Seasons");
        }
    }
}
