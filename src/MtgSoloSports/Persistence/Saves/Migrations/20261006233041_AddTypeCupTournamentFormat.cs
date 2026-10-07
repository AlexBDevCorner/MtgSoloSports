using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtgSoloSports.Persistence.Saves.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeCupTournamentFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_CreatureType",
                table: "TypeCupTeamStandings");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TeamRank",
                table: "TypeCupTeamStandings");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamRounds_SourceSeasonId_GroupNumber_RoundNumber",
                table: "TypeCupTeamRounds");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_GroupNumber_GroupRank",
                table: "TypeCupTeamGroupStandings");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_SaveAthleteId",
                table: "TypeCupTeamGroupStandings");

            migrationBuilder.AddColumn<int>(
                name: "QualificationGroup",
                table: "TypeCupTeamStandings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TournamentPhase",
                table: "TypeCupTeamStandings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualificationGroup",
                table: "TypeCupTeamRounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TournamentPhase",
                table: "TypeCupTeamRounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualificationGroup",
                table: "TypeCupTeamGroupStandings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TournamentPhase",
                table: "TypeCupTeamGroupStandings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TypeCupTournamentDraws",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatureType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    QualificationGroup = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupSize = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldTeamCount = table.Column<int>(type: "INTEGER", nullable: false),
                    QualificationGroupCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalPlacesForGroup = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TournamentFormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RngBeforeState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngBeforeStream = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterState = table.Column<long>(type: "INTEGER", nullable: false),
                    RngAfterStream = table.Column<long>(type: "INTEGER", nullable: false),
                    DrawChecksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypeCupTournamentDraws", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TournamentPhase_QualificationGroup_CreatureType",
                table: "TypeCupTeamStandings",
                columns: new[] { "SourceSeasonId", "TournamentPhase", "QualificationGroup", "CreatureType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TournamentPhase_QualificationGroup_TeamRank",
                table: "TypeCupTeamStandings",
                columns: new[] { "SourceSeasonId", "TournamentPhase", "QualificationGroup", "TeamRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamRounds_SourceSeasonId_TournamentPhase_QualificationGroup_GroupNumber_RoundNumber",
                table: "TypeCupTeamRounds",
                columns: new[] { "SourceSeasonId", "TournamentPhase", "QualificationGroup", "GroupNumber", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_TournamentPhase_QualificationGroup_GroupNumber_GroupRank",
                table: "TypeCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "TournamentPhase", "QualificationGroup", "GroupNumber", "GroupRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_TournamentPhase_QualificationGroup_SaveAthleteId",
                table: "TypeCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "TournamentPhase", "QualificationGroup", "SaveAthleteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTournamentDraws_SourceSeasonId",
                table: "TypeCupTournamentDraws",
                column: "SourceSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTournamentDraws_SourceSeasonId_CreatureType",
                table: "TypeCupTournamentDraws",
                columns: new[] { "SourceSeasonId", "CreatureType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTournamentDraws_SourceSeasonId_QualificationGroup",
                table: "TypeCupTournamentDraws",
                columns: new[] { "SourceSeasonId", "QualificationGroup" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TypeCupTournamentDraws");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TournamentPhase_QualificationGroup_CreatureType",
                table: "TypeCupTeamStandings");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TournamentPhase_QualificationGroup_TeamRank",
                table: "TypeCupTeamStandings");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamRounds_SourceSeasonId_TournamentPhase_QualificationGroup_GroupNumber_RoundNumber",
                table: "TypeCupTeamRounds");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_TournamentPhase_QualificationGroup_GroupNumber_GroupRank",
                table: "TypeCupTeamGroupStandings");

            migrationBuilder.DropIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_TournamentPhase_QualificationGroup_SaveAthleteId",
                table: "TypeCupTeamGroupStandings");

            migrationBuilder.DropColumn(
                name: "QualificationGroup",
                table: "TypeCupTeamStandings");

            migrationBuilder.DropColumn(
                name: "TournamentPhase",
                table: "TypeCupTeamStandings");

            migrationBuilder.DropColumn(
                name: "QualificationGroup",
                table: "TypeCupTeamRounds");

            migrationBuilder.DropColumn(
                name: "TournamentPhase",
                table: "TypeCupTeamRounds");

            migrationBuilder.DropColumn(
                name: "QualificationGroup",
                table: "TypeCupTeamGroupStandings");

            migrationBuilder.DropColumn(
                name: "TournamentPhase",
                table: "TypeCupTeamGroupStandings");

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_CreatureType",
                table: "TypeCupTeamStandings",
                columns: new[] { "SourceSeasonId", "CreatureType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamStandings_SourceSeasonId_TeamRank",
                table: "TypeCupTeamStandings",
                columns: new[] { "SourceSeasonId", "TeamRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamRounds_SourceSeasonId_GroupNumber_RoundNumber",
                table: "TypeCupTeamRounds",
                columns: new[] { "SourceSeasonId", "GroupNumber", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_GroupNumber_GroupRank",
                table: "TypeCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "GroupNumber", "GroupRank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TypeCupTeamGroupStandings_SourceSeasonId_SaveAthleteId",
                table: "TypeCupTeamGroupStandings",
                columns: new[] { "SourceSeasonId", "SaveAthleteId" },
                unique: true);
        }
    }
}
