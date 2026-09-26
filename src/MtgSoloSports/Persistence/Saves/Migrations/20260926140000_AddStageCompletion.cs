using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddStageCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsComplete",
                table: "Stages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "StageStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    StageRank = table.Column<int>(type: "INTEGER", nullable: false),
                    StageScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    ChampionshipPointsThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EarnedBonusThousandths = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StageStandings_SeasonId_LeagueId_StageNumber",
                table: "StageStandings",
                columns: new[] { "SeasonId", "LeagueId", "StageNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_StageStandings_SeasonId_LeagueId_StageNumber_SaveAthleteId",
                table: "StageStandings",
                columns: new[] { "SeasonId", "LeagueId", "StageNumber", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageStandings_StageId",
                table: "StageStandings",
                column: "StageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StageStandings");

            migrationBuilder.DropColumn(
                name: "IsComplete",
                table: "Stages");
        }
    }
}
