using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SaveDbContext))]
    [Migration("20261102000000_AddSelectionChaosForm")]
    public partial class AddSelectionChaosForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChaosRaw",
                table: "ColorCupSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChaosNormThousandths",
                table: "ColorCupSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChaosRaw",
                table: "TypeCupSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChaosNormThousandths",
                table: "TypeCupSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite DROP COLUMN via raw SQL (EF Core DropColumnOperation is
            // not supported by the SQLite generator; raw ALTER TABLE works on
            // modern SQLite and keeps downgrade/upgrade healing intact).
            migrationBuilder.Sql("ALTER TABLE ColorCupSelections DROP COLUMN ChaosRaw;");
            migrationBuilder.Sql("ALTER TABLE ColorCupSelections DROP COLUMN ChaosNormThousandths;");
            migrationBuilder.Sql("ALTER TABLE TypeCupSelections DROP COLUMN ChaosRaw;");
            migrationBuilder.Sql("ALTER TABLE TypeCupSelections DROP COLUMN ChaosNormThousandths;");
        }
    }
}
