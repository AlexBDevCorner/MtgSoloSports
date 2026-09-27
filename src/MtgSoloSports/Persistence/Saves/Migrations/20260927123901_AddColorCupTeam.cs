using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddColorCupTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Honours_SeasonId_LeagueId",
                table: "Honours");

            migrationBuilder.CreateTable(
                name: "ColorCupTeamGroupStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupRank = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectionRank = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColorCupTeamGroupStandings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ColorCupTeamRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RngBeforeState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngBeforeStream = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterStream = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadChecksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColorCupTeamRounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ColorCupTeamStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamRank = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamBaseThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Medal = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColorCupTeamStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Honours_SeasonId_LeagueId_SaveAthleteId_Kind",
                table: "Honours",
                columns: new[] { "SeasonId", "LeagueId", "SaveAthleteId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamGroupStandings_SaveAthleteId",
                table: "ColorCupTeamGroupStandings",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamGroupStandings_SourceSeasonId",
                table: "ColorCupTeamGroupStandings",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamGroupStandings_SourceSeasonId_GroupNumber_GroupRank",
                table: "ColorCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "GroupNumber", "GroupRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamGroupStandings_SourceSeasonId_SaveAthleteId",
                table: "ColorCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamRounds_SourceSeasonId",
                table: "ColorCupTeamRounds",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamRounds_SourceSeasonId_GroupNumber_RoundNumber",
                table: "ColorCupTeamRounds",
                columns: new[] { "SourceSeasonId", "GroupNumber", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamStandings_SourceSeasonId",
                table: "ColorCupTeamStandings",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamStandings_SourceSeasonId_SportingColor",
                table: "ColorCupTeamStandings",
                columns: new[] { "SourceSeasonId", "SportingColor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupTeamStandings_SourceSeasonId_TeamRank",
                table: "ColorCupTeamStandings",
                columns: new[] { "SourceSeasonId", "TeamRank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColorCupTeamGroupStandings");

            migrationBuilder.DropTable(
                name: "ColorCupTeamRounds");

            migrationBuilder.DropTable(
                name: "ColorCupTeamStandings");

            migrationBuilder.DropIndex(
                name: "IX_Honours_SeasonId_LeagueId_SaveAthleteId_Kind",
                table: "Honours");

            migrationBuilder.CreateIndex(
                name: "IX_Honours_SeasonId_LeagueId",
                table: "Honours",
                columns: new[] { "SeasonId", "LeagueId" },
                unique: true);
        }
    }
}
