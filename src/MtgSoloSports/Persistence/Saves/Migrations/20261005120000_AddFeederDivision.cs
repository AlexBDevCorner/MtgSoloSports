using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SaveDbContext))]
    [Migration("20261005120000_AddFeederDivision")]
    public partial class AddFeederDivision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leagues_SeasonId_Kind_SportingColor",
                table: "Leagues");

            migrationBuilder.AddColumn<int>(
                name: "FeederDivision",
                table: "Leagues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing save databases predate the division column: every
            // persisted feeder row represents the single v1 feeder tier and
            // surfaces as Feeder 1. Superleague rows keep None (0).
            migrationBuilder.Sql("UPDATE \"Leagues\" SET \"FeederDivision\" = 1 WHERE \"Kind\" = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_Leagues_SeasonId_Kind_FeederDivision_SportingColor",
                table: "Leagues",
                columns: new[] { "SeasonId", "Kind", "FeederDivision", "SportingColor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leagues_SeasonId_Kind_FeederDivision_SportingColor",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "FeederDivision",
                table: "Leagues");

            migrationBuilder.CreateIndex(
                name: "IX_Leagues_SeasonId_Kind_SportingColor",
                table: "Leagues",
                columns: new[] { "SeasonId", "Kind", "SportingColor" },
                unique: true);
        }
    }
}
