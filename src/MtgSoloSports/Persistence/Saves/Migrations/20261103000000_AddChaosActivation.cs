using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SaveDbContext))]
    [Migration("20261103000000_AddChaosActivation")]
    public partial class AddChaosActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChaosCupActivations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ChaosFormatVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChaosCupActivations", x => x.Id);
                    table.CheckConstraint("CK_ChaosCupActivations_SingleRow", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChaosCupActivations");
        }
    }
}
