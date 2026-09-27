using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeCupAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TypeCupNationality",
                table: "SaveAthletes",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TypeCupSelections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatureType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectionRank = table.Column<int>(type: "INTEGER", nullable: false),
                    TypeRank = table.Column<int>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_TypeCupSelections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupSelections_SaveAthleteId",
                table: "TypeCupSelections",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupSelections_SourceSeasonId",
                table: "TypeCupSelections",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupSelections_SourceSeasonId_CreatureType_SaveAthleteId",
                table: "TypeCupSelections",
                columns: new[] { "SourceSeasonId", "CreatureType", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupSelections_SourceSeasonId_CreatureType_SelectionRank",
                table: "TypeCupSelections",
                columns: new[] { "SourceSeasonId", "CreatureType", "SelectionRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupSelections_SourceSeasonId_SaveAthleteId",
                table: "TypeCupSelections",
                columns: new[] { "SourceSeasonId", "SaveAthleteId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TypeCupSelections");

            migrationBuilder.DropColumn(
                name: "TypeCupNationality",
                table: "SaveAthletes");
        }
    }
}
