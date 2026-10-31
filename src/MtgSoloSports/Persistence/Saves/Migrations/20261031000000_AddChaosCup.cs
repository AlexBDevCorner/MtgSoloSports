using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddChaosCup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChaosCupEditions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ChaosFormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldSize = table.Column<int>(type: "INTEGER", nullable: false),
                    RngBeforeState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngBeforeStream = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterStream = table.Column<long>(type: "INTEGER", nullable: false),
                    EditionChecksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChaosCupEditions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChaosCupEntrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceLeagueId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceLeagueKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceFeederDivision = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPoolInvite = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChaosCupEntrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChaosCupDraws",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StageIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    DrawIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupSize = table.Column<int>(type: "INTEGER", nullable: false),
                    StageGroupCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ChaosFormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RngBeforeState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngBeforeStream = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterStream = table.Column<long>(type: "INTEGER", nullable: false),
                    DrawChecksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChaosCupDraws", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChaosCupRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StageIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ChaosFormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RngBeforeState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngBeforeStream = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterStream = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadChecksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChaosCupRounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChaosCupGroupStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StageIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupRank = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAdvanced = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChaosCupGroupStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupEditions_SourceSeasonId",
                table: "ChaosCupEditions",
                column: "SourceSeasonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupEditions_SourceSeasonNumber",
                table: "ChaosCupEditions",
                column: "SourceSeasonNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupEntrants_SourceSeasonId",
                table: "ChaosCupEntrants",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupEntrants_SaveAthleteId",
                table: "ChaosCupEntrants",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupEntrants_SourceSeasonId_SaveAthleteId",
                table: "ChaosCupEntrants",
                columns: new[] { "SourceSeasonId", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupDraws_SourceSeasonId",
                table: "ChaosCupDraws",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupDraws_SourceSeasonId_StageIndex_GroupNumber",
                table: "ChaosCupDraws",
                columns: new[] { "SourceSeasonId", "StageIndex", "GroupNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupDraws_SourceSeasonId_StageIndex_SaveAthleteId",
                table: "ChaosCupDraws",
                columns: new[] { "SourceSeasonId", "StageIndex", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupDraws_SourceSeasonId_StageIndex_GroupNumber_DrawIndex",
                table: "ChaosCupDraws",
                columns: new[] { "SourceSeasonId", "StageIndex", "GroupNumber", "DrawIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupRounds_SourceSeasonId",
                table: "ChaosCupRounds",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupRounds_SourceSeasonId_StageIndex_GroupNumber",
                table: "ChaosCupRounds",
                columns: new[] { "SourceSeasonId", "StageIndex", "GroupNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupRounds_SourceSeasonId_StageIndex_GroupNumber_RoundNumber",
                table: "ChaosCupRounds",
                columns: new[] { "SourceSeasonId", "StageIndex", "GroupNumber", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupGroupStandings_SourceSeasonId",
                table: "ChaosCupGroupStandings",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupGroupStandings_SaveAthleteId",
                table: "ChaosCupGroupStandings",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupGroupStandings_SourceSeasonId_StageIndex_GroupNumber",
                table: "ChaosCupGroupStandings",
                columns: new[] { "SourceSeasonId", "StageIndex", "GroupNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupGroupStandings_SourceSeasonId_StageIndex_SaveAthleteId",
                table: "ChaosCupGroupStandings",
                columns: new[] { "SourceSeasonId", "StageIndex", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChaosCupGroupStandings_SourceSeasonId_StageIndex_GroupNumber_GroupRank",
                table: "ChaosCupGroupStandings",
                columns: new[] { "SourceSeasonId", "StageIndex", "GroupNumber", "GroupRank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChaosCupGroupStandings");

            migrationBuilder.DropTable(
                name: "ChaosCupRounds");

            migrationBuilder.DropTable(
                name: "ChaosCupDraws");

            migrationBuilder.DropTable(
                name: "ChaosCupEntrants");

            migrationBuilder.DropTable(
                name: "ChaosCupEditions");
        }
    }
}
