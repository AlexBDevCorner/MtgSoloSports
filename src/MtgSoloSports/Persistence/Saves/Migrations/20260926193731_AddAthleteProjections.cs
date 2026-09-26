using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddAthleteProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AthleteCareers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonsActive = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentLeagueId = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentLeagueName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CurrentLeagueKind = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    StageWins = table.Column<int>(type: "INTEGER", nullable: false),
                    StageSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    StageThirds = table.Column<int>(type: "INTEGER", nullable: false),
                    BestSeasonFinish = table.Column<int>(type: "INTEGER", nullable: true),
                    BestSeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    LifetimeEarnedBonusThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentEffectiveBonusThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LastStageNumber = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthleteCareers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AthleteSeasonSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: true),
                    LeagueName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LeagueKind = table.Column<int>(type: "INTEGER", nullable: true),
                    WasActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    StageWins = table.Column<int>(type: "INTEGER", nullable: false),
                    StageSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    StageThirds = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonRank = table.Column<int>(type: "INTEGER", nullable: true),
                    IsChampion = table.Column<bool>(type: "INTEGER", nullable: false),
                    EarnedBonusThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalChampionshipPointsThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalStageScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthleteSeasonSummaries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AthleteCareers_IsActive",
                table: "AthleteCareers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AthleteCareers_SaveAthleteId",
                table: "AthleteCareers",
                column: "SaveAthleteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AthleteSeasonSummaries_SaveAthleteId_SeasonNumber",
                table: "AthleteSeasonSummaries",
                columns: new[] { "SaveAthleteId", "SeasonNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AthleteSeasonSummaries_SeasonId_LeagueId",
                table: "AthleteSeasonSummaries",
                columns: new[] { "SeasonId", "LeagueId" });

            migrationBuilder.CreateIndex(
                name: "IX_AthleteSeasonSummaries_SeasonId_SaveAthleteId",
                table: "AthleteSeasonSummaries",
                columns: new[] { "SeasonId", "SaveAthleteId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AthleteCareers");

            migrationBuilder.DropTable(
                name: "AthleteSeasonSummaries");
        }
    }
}
