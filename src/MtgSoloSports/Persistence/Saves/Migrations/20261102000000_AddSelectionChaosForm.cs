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
            // SQLite does not support DROP COLUMN in downgrades used by the
            // healing test; leave the additive columns (harmless extra storage,
            // ignored by older models) so downgrade/upgrade still heals.
        }
    }
}
