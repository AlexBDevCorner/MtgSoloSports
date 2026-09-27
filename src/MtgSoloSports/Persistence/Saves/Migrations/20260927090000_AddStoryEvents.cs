using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoryEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaveAthleteId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StageNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ContextJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoryEvents_SaveAthleteId",
                table: "StoryEvents",
                column: "SaveAthleteId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryEvents_SaveAthleteId_EventType_DedupKey",
                table: "StoryEvents",
                columns: new[] { "SaveAthleteId", "EventType", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoryEvents_SeasonNumber_Id",
                table: "StoryEvents",
                columns: new[] { "SeasonNumber", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoryEvents");
        }
    }
}
