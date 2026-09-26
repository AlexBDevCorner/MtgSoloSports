using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddSeason1Leagues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Leagues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leagues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeasonMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: true),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    DrawIndex = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonMemberships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Seasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    HasSuperleague = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seasons", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leagues_SeasonId",
                table: "Leagues",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_Leagues_SeasonId_Kind_SportingColor",
                table: "Leagues",
                columns: new[] { "SeasonId", "Kind", "SportingColor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeasonMemberships_SeasonId_LeagueId",
                table: "SeasonMemberships",
                columns: new[] { "SeasonId", "LeagueId" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonMemberships_SeasonId_LeagueId_DrawIndex",
                table: "SeasonMemberships",
                columns: new[] { "SeasonId", "LeagueId", "DrawIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonMemberships_SeasonId_SaveAthleteId",
                table: "SeasonMemberships",
                columns: new[] { "SeasonId", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeasonMemberships_SeasonId_SportingColor",
                table: "SeasonMemberships",
                columns: new[] { "SeasonId", "SportingColor" });

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_SeasonNumber",
                table: "Seasons",
                column: "SeasonNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Leagues");

            migrationBuilder.DropTable(
                name: "SeasonMemberships");

            migrationBuilder.DropTable(
                name: "Seasons");
        }
    }
}
