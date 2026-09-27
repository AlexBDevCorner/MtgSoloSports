using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeCupTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TypeCupTeamGroupStandings",
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
                    CreatureType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SelectionRank = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypeCupTeamGroupStandings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TypeCupTeamRounds",
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
                    table.PrimaryKey("PK_TypeCupTeamRounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TypeCupTeamStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatureType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("PK_TypeCupTeamStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SaveAthleteId",
                table: "TypeCupTeamGroupStandings",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId",
                table: "TypeCupTeamGroupStandings",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_GroupNumber_GroupRank",
                table: "TypeCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "GroupNumber", "GroupRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_SaveAthleteId",
                table: "TypeCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamRounds_SourceSeasonId",
                table: "TypeCupTeamRounds",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamRounds_SourceSeasonId_GroupNumber_RoundNumber",
                table: "TypeCupTeamRounds",
                columns: new[] { "SourceSeasonId", "GroupNumber", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId",
                table: "TypeCupTeamStandings",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_CreatureType",
                table: "TypeCupTeamStandings",
                columns: new[] { "SourceSeasonId", "CreatureType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TeamRank",
                table: "TypeCupTeamStandings",
                columns: new[] { "SourceSeasonId", "TeamRank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TypeCupTeamGroupStandings");

            migrationBuilder.DropTable(
                name: "TypeCupTeamRounds");

            migrationBuilder.DropTable(
                name: "TypeCupTeamStandings");
        }
    }
}
