using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddSaveAthletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SaveAthletes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SportingColor = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatureTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FrontColors = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ManaCost = table.Column<string>(type: "TEXT", nullable: false),
                    TypeLine = table.Column<string>(type: "TEXT", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SetCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IsArtifact = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasDevoid = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasHybridMana = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaveAthletes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaveAthletes_Name",
                table: "SaveAthletes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaveAthletes_SportingColor",
                table: "SaveAthletes",
                column: "SportingColor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaveAthletes");
        }
    }
}
