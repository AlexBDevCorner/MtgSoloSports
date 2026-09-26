using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class InitialSaveSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RngStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AlgorithmVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<long>(type: "INTEGER", nullable: false),
                    Stream = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RngStates", x => x.Id);
                    table.CheckConstraint("CK_RngStates_SingleRow", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "RulesSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RulesSnapshots", x => x.Id);
                    table.CheckConstraint("CK_RulesSnapshots_SingleRow", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "SaveMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentSeason = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaveMetadata", x => x.Id);
                    table.CheckConstraint("CK_SaveMetadata_SingleRow", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RngStates");

            migrationBuilder.DropTable(
                name: "RulesSnapshots");

            migrationBuilder.DropTable(
                name: "SaveMetadata");
        }
    }
}
