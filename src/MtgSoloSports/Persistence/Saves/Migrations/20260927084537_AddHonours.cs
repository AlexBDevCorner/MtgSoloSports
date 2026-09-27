using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddHonours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Honours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LeagueKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Honours", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Honours_SaveAthleteId_Kind",
                table: "Honours",
                columns: new[] { "SaveAthleteId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Honours_SeasonId_LeagueId",
                table: "Honours",
                columns: new[] { "SeasonId", "LeagueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Honours_SeasonNumber",
                table: "Honours",
                column: "SeasonNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Honours");
        }
    }
}
