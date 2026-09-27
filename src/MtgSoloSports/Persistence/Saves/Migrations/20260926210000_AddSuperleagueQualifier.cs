using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperleagueQualifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualifierRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_QualifierRounds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId",
                table: "QualifierRounds",
                columns: new[] { "FromSeasonId", "ToSeasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_RoundNumber",
                table: "QualifierRounds",
                columns: new[] { "FromSeasonId", "ToSeasonId", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "QualifierStandings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    QualifierRank = table.Column<int>(type: "INTEGER", nullable: false),
                    QualifierScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseScoreThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundPlaceCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsQualified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    FromLeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromSeasonRank = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualifierStandings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId",
                table: "QualifierStandings",
                columns: new[] { "FromSeasonId", "ToSeasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierRank",
                table: "QualifierStandings",
                columns: new[] { "FromSeasonId", "ToSeasonId", "QualifierRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_SaveAthleteId",
                table: "QualifierStandings",
                columns: new[] { "FromSeasonId", "ToSeasonId", "SaveAthleteId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualifierRounds");

            migrationBuilder.DropTable(
                name: "QualifierStandings");
        }
    }
}
