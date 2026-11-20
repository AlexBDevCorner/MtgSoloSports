using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogAthletes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatureTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsArtifact = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasDevoid = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasHybridMana = table.Column<bool>(type: "INTEGER", nullable: false),
                    FrontColors = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ManaCost = table.Column<string>(type: "TEXT", nullable: false),
                    TypeLine = table.Column<string>(type: "TEXT", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SetCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogAthletes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogAthletes_Name",
                table: "CatalogAthletes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogAthletes");
        }
    }
}
