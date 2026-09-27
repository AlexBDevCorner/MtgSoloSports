using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddColorCupSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColorCupSelections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectionRank = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalRatingThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BonusNormThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    PerformanceNormThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    FormNormThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    PrestigeNormThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    BonusRawThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    PerformanceRawThousandths = table.Column<int>(type: "INTEGER", nullable: false),
                    FormRaw = table.Column<int>(type: "INTEGER", nullable: false),
                    PrestigeRaw = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColorCupSelections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupSelections_SaveAthleteId",
                table: "ColorCupSelections",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupSelections_SourceSeasonId",
                table: "ColorCupSelections",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupSelections_SourceSeasonId_SportingColor_SaveAthleteId",
                table: "ColorCupSelections",
                columns: new[] { "SourceSeasonId", "SportingColor", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColorCupSelections_SourceSeasonId_SportingColor_SelectionRank",
                table: "ColorCupSelections",
                columns: new[] { "SourceSeasonId", "SportingColor", "SelectionRank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColorCupSelections");
        }
    }
}
