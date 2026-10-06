using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SaveDbContext))]
    [Migration("20261006090000_AddQualifierBoundary")]
    public partial class AddQualifierBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_RoundNumber",
                table: "QualifierRounds");

            migrationBuilder.DropIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierRank",
                table: "QualifierStandings");

            migrationBuilder.DropIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_SaveAthleteId",
                table: "QualifierStandings");

            migrationBuilder.AddColumn<int>(
                name: "QualifierBoundary",
                table: "QualifierRounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualifierSportingColor",
                table: "QualifierRounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<int>(
                name: "QualifierBoundary",
                table: "QualifierStandings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualifierSportingColor",
                table: "QualifierStandings",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1);

            // Existing rows predate MSS-058 and represent the single
            // Superleague qualifier: boundary Superleague (0), color sentinel -1.
            migrationBuilder.Sql("UPDATE \"QualifierRounds\" SET \"QualifierBoundary\" = 0, \"QualifierSportingColor\" = -1;");
            migrationBuilder.Sql("UPDATE \"QualifierStandings\" SET \"QualifierBoundary\" = 0, \"QualifierSportingColor\" = -1;");

            migrationBuilder.CreateIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor",
                table: "QualifierRounds",
                columns: new[] { "FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor" });

            migrationBuilder.CreateIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_RoundNumber",
                table: "QualifierRounds",
                columns: new[] { "FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor",
                table: "QualifierStandings",
                columns: new[] { "FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor" });

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_QualifierRank",
                table: "QualifierStandings",
                columns: new[] { "FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor", "QualifierRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_SaveAthleteId",
                table: "QualifierStandings",
                columns: new[] { "FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualifierStandings_SaveAthleteId",
                table: "QualifierStandings",
                column: "SaveAthleteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor",
                table: "QualifierRounds");

            migrationBuilder.DropIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_RoundNumber",
                table: "QualifierRounds");

            migrationBuilder.DropIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor",
                table: "QualifierStandings");

            migrationBuilder.DropIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_QualifierRank",
                table: "QualifierStandings");

            migrationBuilder.DropIndex(
                name: "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_SaveAthleteId",
                table: "QualifierStandings");

            migrationBuilder.DropIndex(
                name: "IX_QualifierStandings_SaveAthleteId",
                table: "QualifierStandings");

            migrationBuilder.DropColumn(
                name: "QualifierBoundary",
                table: "QualifierRounds");

            migrationBuilder.DropColumn(
                name: "QualifierSportingColor",
                table: "QualifierRounds");

            migrationBuilder.DropColumn(
                name: "QualifierBoundary",
                table: "QualifierStandings");

            migrationBuilder.DropColumn(
                name: "QualifierSportingColor",
                table: "QualifierStandings");

            migrationBuilder.CreateIndex(
                name: "IX_QualifierRounds_FromSeasonId_ToSeasonId_RoundNumber",
                table: "QualifierRounds",
                columns: new[] { "FromSeasonId", "ToSeasonId", "RoundNumber" },
                unique: true);

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
    }
}
