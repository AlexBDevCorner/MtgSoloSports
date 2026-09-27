using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SaveDbContext))]
    [Migration("20260927120000_AddColorCupIndividual")]
    public partial class AddColorCupIndividual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColorCupIndividualRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_ColorCupIndividualRounds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupIndividualRounds_SourceSeasonId",
                table: "ColorCupIndividualRounds",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupIndividualRounds_SourceSeasonId_RoundNumber",
                table: "ColorCupIndividualRounds",
                columns: new[] { "SourceSeasonId", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "ColorCupIndividualStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    CupRank = table.Column<int>(type: "INTEGER", nullable: false),
                    CupScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Medal = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectionRank = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColorCupIndividualStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupIndividualStandings_SaveAthleteId",
                table: "ColorCupIndividualStandings",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupIndividualStandings_SourceSeasonId",
                table: "ColorCupIndividualStandings",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupIndividualStandings_SourceSeasonId_CupRank",
                table: "ColorCupIndividualStandings",
                columns: new[] { "SourceSeasonId", "CupRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupIndividualStandings_SourceSeasonId_SaveAthleteId",
                table: "ColorCupIndividualStandings",
                columns: new[] { "SourceSeasonId", "SaveAthleteId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColorCupIndividualRounds");

            migrationBuilder.DropTable(
                name: "ColorCupIndividualStandings");
        }
    }
}
