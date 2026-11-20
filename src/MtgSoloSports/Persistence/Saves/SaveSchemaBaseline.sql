-- save-schema-baseline-through: 20261105000000_AddColorCupIndividualFormat
CREATE TABLE "AthleteCareers" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AthleteCareers" PRIMARY KEY AUTOINCREMENT,
    "SaveAthleteId" INTEGER NOT NULL,
    "SeasonsActive" INTEGER NOT NULL,
    "CurrentLeagueId" INTEGER NULL,
    "CurrentLeagueName" TEXT NULL,
    "CurrentLeagueKind" INTEGER NULL,
    "IsActive" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "StageWins" INTEGER NOT NULL,
    "StageSeconds" INTEGER NOT NULL,
    "StageThirds" INTEGER NOT NULL,
    "BestSeasonFinish" INTEGER NULL,
    "BestSeasonNumber" INTEGER NULL,
    "LifetimeEarnedBonusThousandths" INTEGER NOT NULL,
    "CurrentEffectiveBonusThousandths" INTEGER NOT NULL,
    "LastSeasonNumber" INTEGER NOT NULL,
    "LastStageNumber" INTEGER NOT NULL
);
CREATE TABLE "AthleteSeasonSummaries" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AthleteSeasonSummaries" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "SeasonNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "LeagueId" INTEGER NULL,
    "LeagueName" TEXT NULL,
    "LeagueKind" INTEGER NULL,
    "WasActive" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "StageWins" INTEGER NOT NULL,
    "StageSeconds" INTEGER NOT NULL,
    "StageThirds" INTEGER NOT NULL,
    "SeasonRank" INTEGER NULL,
    "IsChampion" INTEGER NOT NULL,
    "EarnedBonusThousandths" INTEGER NOT NULL,
    "TotalChampionshipPointsThousandths" INTEGER NOT NULL,
    "TotalStageScoreThousandths" INTEGER NOT NULL,
    "TotalBaseScoreThousandths" INTEGER NOT NULL
);
CREATE TABLE "ChaosCupActivations" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChaosCupActivations" PRIMARY KEY,
    "EffectiveFromSeasonNumber" INTEGER NOT NULL,
    "ChaosFormatVersion" INTEGER NOT NULL,
    CONSTRAINT "CK_ChaosCupActivations_SingleRow" CHECK ("Id" = 1)
);
CREATE TABLE "ChaosCupDraws" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChaosCupDraws" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "StageIndex" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "DrawIndex" INTEGER NOT NULL,
    "GroupSize" INTEGER NOT NULL,
    "StageGroupCount" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "ChaosFormatVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "DrawChecksum" TEXT NOT NULL
);
CREATE TABLE "ChaosCupEditions" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChaosCupEditions" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "ChaosFormatVersion" INTEGER NOT NULL,
    "FieldSize" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "EditionChecksum" TEXT NOT NULL
);
CREATE TABLE "ChaosCupEntrants" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChaosCupEntrants" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "SourceLeagueId" INTEGER NULL,
    "SourceLeagueKind" INTEGER NOT NULL,
    "SourceFeederDivision" INTEGER NOT NULL,
    "IsPoolInvite" INTEGER NOT NULL
, "EffectiveBonusThousandths" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "ChaosCupGroupStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChaosCupGroupStandings" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "StageIndex" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "GroupRank" INTEGER NOT NULL,
    "GroupScoreThousandths" INTEGER NOT NULL,
    "BaseScoreThousandths" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "IsAdvanced" INTEGER NOT NULL
);
CREATE TABLE "ChaosCupRounds" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChaosCupRounds" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "StageIndex" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "RoundNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "ChaosFormatVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PayloadChecksum" TEXT NOT NULL
);
CREATE TABLE "ColorCupIndividualRounds" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ColorCupIndividualRounds" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "RoundNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PayloadChecksum" TEXT NOT NULL
, "FormatVersion" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "ColorCupIndividualStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ColorCupIndividualStandings" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "CupRank" INTEGER NOT NULL,
    "CupScoreThousandths" INTEGER NOT NULL,
    "BaseScoreThousandths" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "Medal" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "SelectionRank" INTEGER NOT NULL
);
CREATE TABLE "ColorCupSelections" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ColorCupSelections" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "SelectionRank" INTEGER NOT NULL,
    "FinalRatingThousandths" INTEGER NOT NULL,
    "BonusNormThousandths" INTEGER NOT NULL,
    "PerformanceNormThousandths" INTEGER NOT NULL,
    "FormNormThousandths" INTEGER NOT NULL,
    "PrestigeNormThousandths" INTEGER NOT NULL,
    "BonusRawThousandths" INTEGER NOT NULL,
    "PerformanceRawThousandths" INTEGER NOT NULL,
    "FormRaw" INTEGER NOT NULL,
    "PrestigeRaw" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL
, "ChaosRaw" INTEGER NOT NULL DEFAULT 0, "ChaosNormThousandths" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "ColorCupTeamGroupStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ColorCupTeamGroupStandings" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "GroupRank" INTEGER NOT NULL,
    "GroupScoreThousandths" INTEGER NOT NULL,
    "BaseScoreThousandths" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "SelectionRank" INTEGER NOT NULL
);
CREATE TABLE "ColorCupTeamRounds" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ColorCupTeamRounds" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "RoundNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PayloadChecksum" TEXT NOT NULL
);
CREATE TABLE "ColorCupTeamStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ColorCupTeamStandings" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "TeamRank" INTEGER NOT NULL,
    "TeamScoreThousandths" INTEGER NOT NULL,
    "TeamBaseThousandths" INTEGER NOT NULL,
    "GroupWins" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "GroupPlaceCountsJson" TEXT NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "Medal" INTEGER NOT NULL
);
CREATE TABLE "CupSelectionFormulaActivations" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_CupSelectionFormulaActivations" PRIMARY KEY,
    "EffectiveFromSeasonNumber" INTEGER NOT NULL,
    "FormulaVersion" INTEGER NOT NULL,
    CONSTRAINT "CK_CupSelectionFormulaActivations_SingleRow" CHECK ("Id" = 1)
);
CREATE TABLE "CupSelectionReports" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_CupSelectionReports" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "Cup" TEXT NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL
);
CREATE TABLE "Honours" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Honours" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "SeasonNumber" INTEGER NOT NULL,
    "LeagueId" INTEGER NOT NULL,
    "LeagueName" TEXT NOT NULL,
    "LeagueKind" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "Kind" INTEGER NOT NULL
);
CREATE TABLE "Leagues" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Leagues" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "Kind" INTEGER NOT NULL,
    "Name" TEXT NOT NULL
, "FeederDivision" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "Movements" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Movements" PRIMARY KEY AUTOINCREMENT,
    "SaveAthleteId" INTEGER NOT NULL,
    "FromSeasonId" INTEGER NOT NULL,
    "ToSeasonId" INTEGER NOT NULL,
    "FromLeagueId" INTEGER NOT NULL,
    "ToLeagueId" INTEGER NOT NULL,
    "Kind" INTEGER NOT NULL,
    "FromSeasonRank" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL
);
CREATE TABLE "QualifierRounds" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_QualifierRounds" PRIMARY KEY AUTOINCREMENT,
    "FromSeasonId" INTEGER NOT NULL,
    "ToSeasonId" INTEGER NOT NULL,
    "RoundNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PayloadChecksum" TEXT NOT NULL
, "QualifierBoundary" INTEGER NOT NULL DEFAULT 0, "QualifierSportingColor" INTEGER NOT NULL DEFAULT -1);
CREATE TABLE "QualifierStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_QualifierStandings" PRIMARY KEY AUTOINCREMENT,
    "FromSeasonId" INTEGER NOT NULL,
    "ToSeasonId" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "QualifierRank" INTEGER NOT NULL,
    "QualifierScoreThousandths" INTEGER NOT NULL,
    "BaseScoreThousandths" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "IsQualified" INTEGER NOT NULL,
    "Role" INTEGER NOT NULL,
    "FromLeagueId" INTEGER NOT NULL,
    "FromSeasonRank" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL
, "QualifierBoundary" INTEGER NOT NULL DEFAULT 0, "QualifierSportingColor" INTEGER NOT NULL DEFAULT -1);
CREATE TABLE "RngStates" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_RngStates" PRIMARY KEY,
    "Algorithm" TEXT NOT NULL,
    "AlgorithmVersion" INTEGER NOT NULL,
    "State" INTEGER NOT NULL,
    "Stream" INTEGER NOT NULL,
    CONSTRAINT "CK_RngStates_SingleRow" CHECK ("Id" = 1)
);
CREATE TABLE "Rounds" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Rounds" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "LeagueId" INTEGER NOT NULL,
    "StageId" INTEGER NOT NULL,
    "StageNumber" INTEGER NOT NULL,
    "RoundNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PayloadChecksum" TEXT NOT NULL
);
CREATE TABLE "RulesSnapshots" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_RulesSnapshots" PRIMARY KEY,
    "RulesVersion" INTEGER NOT NULL,
    "RulesJson" TEXT NOT NULL,
    CONSTRAINT "CK_RulesSnapshots_SingleRow" CHECK ("Id" = 1)
);
CREATE TABLE "SaveAthletes" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SaveAthletes" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "CreatureTypesJson" TEXT NOT NULL,
    "FrontColors" TEXT NOT NULL,
    "ManaCost" TEXT NOT NULL,
    "TypeLine" TEXT NOT NULL,
    "ImageUrl" TEXT NULL,
    "SetCode" TEXT NULL,
    "IsArtifact" INTEGER NOT NULL,
    "HasDevoid" INTEGER NOT NULL,
    "HasHybridMana" INTEGER NOT NULL,
    "Status" INTEGER NOT NULL
, "TypeCupNationality" TEXT NULL);
CREATE TABLE "SaveMetadata" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SaveMetadata" PRIMARY KEY,
    "SaveId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "SchemaVersion" INTEGER NOT NULL,
    "CurrentSeason" INTEGER NOT NULL,
    "Phase" TEXT NOT NULL,
    CONSTRAINT "CK_SaveMetadata_SingleRow" CHECK ("Id" = 1)
);
CREATE TABLE "SeasonMemberships" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SeasonMemberships" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "LeagueId" INTEGER NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "SportingColor" INTEGER NOT NULL,
    "DrawIndex" INTEGER NOT NULL
);
CREATE TABLE "SeasonStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SeasonStandings" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "LeagueId" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "SeasonRank" INTEGER NOT NULL,
    "TotalChampionshipPointsThousandths" INTEGER NOT NULL,
    "TotalStageScoreThousandths" INTEGER NOT NULL,
    "TotalBaseScoreThousandths" INTEGER NOT NULL,
    "StageWins" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "StagePlaceCountsJson" TEXT NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "IsChampion" INTEGER NOT NULL
);
CREATE TABLE "Seasons" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Seasons" PRIMARY KEY AUTOINCREMENT,
    "SeasonNumber" INTEGER NOT NULL,
    "HasSuperleague" INTEGER NOT NULL
, "IsComplete" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "StageStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_StageStandings" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "LeagueId" INTEGER NOT NULL,
    "StageId" INTEGER NOT NULL,
    "StageNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "StageRank" INTEGER NOT NULL,
    "StageScoreThousandths" INTEGER NOT NULL,
    "BaseScoreThousandths" INTEGER NOT NULL,
    "ChampionshipPointsThousandths" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "EarnedBonusThousandths" INTEGER NOT NULL
);
CREATE TABLE "Stages" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Stages" PRIMARY KEY AUTOINCREMENT,
    "SeasonId" INTEGER NOT NULL,
    "LeagueId" INTEGER NOT NULL,
    "StageNumber" INTEGER NOT NULL,
    "CompletedRounds" INTEGER NOT NULL
, "IsComplete" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "StoryEvents" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_StoryEvents" PRIMARY KEY AUTOINCREMENT,
    "SaveAthleteId" INTEGER NOT NULL,
    "EventType" TEXT NOT NULL,
    "DedupKey" TEXT NOT NULL,
    "SeasonNumber" INTEGER NOT NULL,
    "StageNumber" INTEGER NULL,
    "ContextJson" TEXT NOT NULL
);
CREATE TABLE "TypeCupSelections" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TypeCupSelections" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "CreatureType" TEXT NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "SelectionRank" INTEGER NOT NULL,
    "TypeRank" INTEGER NOT NULL,
    "FinalRatingThousandths" INTEGER NOT NULL,
    "BonusNormThousandths" INTEGER NOT NULL,
    "PerformanceNormThousandths" INTEGER NOT NULL,
    "FormNormThousandths" INTEGER NOT NULL,
    "PrestigeNormThousandths" INTEGER NOT NULL,
    "BonusRawThousandths" INTEGER NOT NULL,
    "PerformanceRawThousandths" INTEGER NOT NULL,
    "FormRaw" INTEGER NOT NULL,
    "PrestigeRaw" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL
, "ChaosRaw" INTEGER NOT NULL DEFAULT 0, "ChaosNormThousandths" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "TypeCupTeamGroupStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TypeCupTeamGroupStandings" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "SaveAthleteId" INTEGER NOT NULL,
    "GroupRank" INTEGER NOT NULL,
    "GroupScoreThousandths" INTEGER NOT NULL,
    "BaseScoreThousandths" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "CreatureType" TEXT NOT NULL,
    "SelectionRank" INTEGER NOT NULL
, "QualificationGroup" INTEGER NOT NULL DEFAULT 0, "TournamentPhase" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "TypeCupTeamRounds" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TypeCupTeamRounds" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "GroupNumber" INTEGER NOT NULL,
    "RoundNumber" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "PayloadChecksum" TEXT NOT NULL
, "QualificationGroup" INTEGER NOT NULL DEFAULT 0, "TournamentPhase" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "TypeCupTeamStandings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TypeCupTeamStandings" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "CreatureType" TEXT NOT NULL,
    "TeamRank" INTEGER NOT NULL,
    "TeamScoreThousandths" INTEGER NOT NULL,
    "TeamBaseThousandths" INTEGER NOT NULL,
    "GroupWins" INTEGER NOT NULL,
    "RoundWins" INTEGER NOT NULL,
    "GroupPlaceCountsJson" TEXT NOT NULL,
    "RoundPlaceCountsJson" TEXT NOT NULL,
    "Medal" INTEGER NOT NULL
, "QualificationGroup" INTEGER NOT NULL DEFAULT 0, "TournamentPhase" INTEGER NOT NULL DEFAULT 0);
CREATE TABLE "TypeCupTournamentDraws" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TypeCupTournamentDraws" PRIMARY KEY AUTOINCREMENT,
    "SourceSeasonId" INTEGER NOT NULL,
    "SourceSeasonNumber" INTEGER NOT NULL,
    "CreatureType" TEXT NOT NULL,
    "QualificationGroup" INTEGER NOT NULL,
    "GroupSize" INTEGER NOT NULL,
    "FieldTeamCount" INTEGER NOT NULL,
    "QualificationGroupCount" INTEGER NOT NULL,
    "FinalPlacesForGroup" INTEGER NOT NULL,
    "RulesVersion" INTEGER NOT NULL,
    "TournamentFormatVersion" INTEGER NOT NULL,
    "RngBeforeState" INTEGER NOT NULL,
    "RngBeforeStream" INTEGER NOT NULL,
    "RngAfterState" INTEGER NOT NULL,
    "RngAfterStream" INTEGER NOT NULL,
    "DrawChecksum" TEXT NOT NULL
);
CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);
CREATE TABLE "__EFMigrationsLock" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
    "Timestamp" TEXT NOT NULL
);
CREATE INDEX "IX_AthleteCareers_IsActive" ON "AthleteCareers" ("IsActive");
CREATE UNIQUE INDEX "IX_AthleteCareers_SaveAthleteId" ON "AthleteCareers" ("SaveAthleteId");
CREATE INDEX "IX_AthleteSeasonSummaries_SaveAthleteId_SeasonNumber" ON "AthleteSeasonSummaries" ("SaveAthleteId", "SeasonNumber");
CREATE INDEX "IX_AthleteSeasonSummaries_SeasonId_LeagueId" ON "AthleteSeasonSummaries" ("SeasonId", "LeagueId");
CREATE UNIQUE INDEX "IX_AthleteSeasonSummaries_SeasonId_SaveAthleteId" ON "AthleteSeasonSummaries" ("SeasonId", "SaveAthleteId");
CREATE INDEX "IX_ChaosCupDraws_SourceSeasonId" ON "ChaosCupDraws" ("SourceSeasonId");
CREATE INDEX "IX_ChaosCupDraws_SourceSeasonId_StageIndex_GroupNumber" ON "ChaosCupDraws" ("SourceSeasonId", "StageIndex", "GroupNumber");
CREATE UNIQUE INDEX "IX_ChaosCupDraws_SourceSeasonId_StageIndex_GroupNumber_DrawIndex" ON "ChaosCupDraws" ("SourceSeasonId", "StageIndex", "GroupNumber", "DrawIndex");
CREATE UNIQUE INDEX "IX_ChaosCupDraws_SourceSeasonId_StageIndex_SaveAthleteId" ON "ChaosCupDraws" ("SourceSeasonId", "StageIndex", "SaveAthleteId");
CREATE UNIQUE INDEX "IX_ChaosCupEditions_SourceSeasonId" ON "ChaosCupEditions" ("SourceSeasonId");
CREATE INDEX "IX_ChaosCupEditions_SourceSeasonNumber" ON "ChaosCupEditions" ("SourceSeasonNumber");
CREATE INDEX "IX_ChaosCupEntrants_SaveAthleteId" ON "ChaosCupEntrants" ("SaveAthleteId");
CREATE INDEX "IX_ChaosCupEntrants_SourceSeasonId" ON "ChaosCupEntrants" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ChaosCupEntrants_SourceSeasonId_SaveAthleteId" ON "ChaosCupEntrants" ("SourceSeasonId", "SaveAthleteId");
CREATE INDEX "IX_ChaosCupGroupStandings_SaveAthleteId" ON "ChaosCupGroupStandings" ("SaveAthleteId");
CREATE INDEX "IX_ChaosCupGroupStandings_SourceSeasonId" ON "ChaosCupGroupStandings" ("SourceSeasonId");
CREATE INDEX "IX_ChaosCupGroupStandings_SourceSeasonId_StageIndex_GroupNumber" ON "ChaosCupGroupStandings" ("SourceSeasonId", "StageIndex", "GroupNumber");
CREATE UNIQUE INDEX "IX_ChaosCupGroupStandings_SourceSeasonId_StageIndex_GroupNumber_GroupRank" ON "ChaosCupGroupStandings" ("SourceSeasonId", "StageIndex", "GroupNumber", "GroupRank");
CREATE UNIQUE INDEX "IX_ChaosCupGroupStandings_SourceSeasonId_StageIndex_SaveAthleteId" ON "ChaosCupGroupStandings" ("SourceSeasonId", "StageIndex", "SaveAthleteId");
CREATE INDEX "IX_ChaosCupRounds_SourceSeasonId" ON "ChaosCupRounds" ("SourceSeasonId");
CREATE INDEX "IX_ChaosCupRounds_SourceSeasonId_StageIndex_GroupNumber" ON "ChaosCupRounds" ("SourceSeasonId", "StageIndex", "GroupNumber");
CREATE UNIQUE INDEX "IX_ChaosCupRounds_SourceSeasonId_StageIndex_GroupNumber_RoundNumber" ON "ChaosCupRounds" ("SourceSeasonId", "StageIndex", "GroupNumber", "RoundNumber");
CREATE INDEX "IX_ColorCupIndividualRounds_SourceSeasonId" ON "ColorCupIndividualRounds" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ColorCupIndividualRounds_SourceSeasonId_RoundNumber" ON "ColorCupIndividualRounds" ("SourceSeasonId", "RoundNumber");
CREATE INDEX "IX_ColorCupIndividualStandings_SaveAthleteId" ON "ColorCupIndividualStandings" ("SaveAthleteId");
CREATE INDEX "IX_ColorCupIndividualStandings_SourceSeasonId" ON "ColorCupIndividualStandings" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ColorCupIndividualStandings_SourceSeasonId_CupRank" ON "ColorCupIndividualStandings" ("SourceSeasonId", "CupRank");
CREATE UNIQUE INDEX "IX_ColorCupIndividualStandings_SourceSeasonId_SaveAthleteId" ON "ColorCupIndividualStandings" ("SourceSeasonId", "SaveAthleteId");
CREATE INDEX "IX_ColorCupSelections_SaveAthleteId" ON "ColorCupSelections" ("SaveAthleteId");
CREATE INDEX "IX_ColorCupSelections_SourceSeasonId" ON "ColorCupSelections" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ColorCupSelections_SourceSeasonId_SportingColor_SaveAthleteId" ON "ColorCupSelections" ("SourceSeasonId", "SportingColor", "SaveAthleteId");
CREATE UNIQUE INDEX "IX_ColorCupSelections_SourceSeasonId_SportingColor_SelectionRank" ON "ColorCupSelections" ("SourceSeasonId", "SportingColor", "SelectionRank");
CREATE INDEX "IX_ColorCupTeamGroupStandings_SaveAthleteId" ON "ColorCupTeamGroupStandings" ("SaveAthleteId");
CREATE INDEX "IX_ColorCupTeamGroupStandings_SourceSeasonId" ON "ColorCupTeamGroupStandings" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ColorCupTeamGroupStandings_SourceSeasonId_GroupNumber_GroupRank" ON "ColorCupTeamGroupStandings" ("SourceSeasonId", "GroupNumber", "GroupRank");
CREATE UNIQUE INDEX "IX_ColorCupTeamGroupStandings_SourceSeasonId_SaveAthleteId" ON "ColorCupTeamGroupStandings" ("SourceSeasonId", "SaveAthleteId");
CREATE INDEX "IX_ColorCupTeamRounds_SourceSeasonId" ON "ColorCupTeamRounds" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ColorCupTeamRounds_SourceSeasonId_GroupNumber_RoundNumber" ON "ColorCupTeamRounds" ("SourceSeasonId", "GroupNumber", "RoundNumber");
CREATE INDEX "IX_ColorCupTeamStandings_SourceSeasonId" ON "ColorCupTeamStandings" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_ColorCupTeamStandings_SourceSeasonId_SportingColor" ON "ColorCupTeamStandings" ("SourceSeasonId", "SportingColor");
CREATE UNIQUE INDEX "IX_ColorCupTeamStandings_SourceSeasonId_TeamRank" ON "ColorCupTeamStandings" ("SourceSeasonId", "TeamRank");
CREATE UNIQUE INDEX "IX_CupSelectionReports_SourceSeasonId" ON "CupSelectionReports" ("SourceSeasonId");
CREATE INDEX "IX_Honours_SaveAthleteId_Kind" ON "Honours" ("SaveAthleteId", "Kind");
CREATE UNIQUE INDEX "IX_Honours_SeasonId_LeagueId_SaveAthleteId_Kind" ON "Honours" ("SeasonId", "LeagueId", "SaveAthleteId", "Kind");
CREATE INDEX "IX_Honours_SeasonNumber" ON "Honours" ("SeasonNumber");
CREATE INDEX "IX_Leagues_SeasonId" ON "Leagues" ("SeasonId");
CREATE UNIQUE INDEX "IX_Leagues_SeasonId_Kind_FeederDivision_SportingColor" ON "Leagues" ("SeasonId", "Kind", "FeederDivision", "SportingColor");
CREATE INDEX "IX_Movements_FromSeasonId_FromLeagueId" ON "Movements" ("FromSeasonId", "FromLeagueId");
CREATE INDEX "IX_Movements_SaveAthleteId_ToSeasonId_Kind" ON "Movements" ("SaveAthleteId", "ToSeasonId", "Kind");
CREATE UNIQUE INDEX "IX_Movements_ToSeasonId_SaveAthleteId_Kind" ON "Movements" ("ToSeasonId", "SaveAthleteId", "Kind");
CREATE INDEX "IX_Movements_ToSeasonId_ToLeagueId" ON "Movements" ("ToSeasonId", "ToLeagueId");
CREATE INDEX "IX_QualifierRounds_FromSeasonId_ToSeasonId" ON "QualifierRounds" ("FromSeasonId", "ToSeasonId");
CREATE INDEX "IX_QualifierRounds_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor" ON "QualifierRounds" ("FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor");
CREATE UNIQUE INDEX "IX_QualifierRounds_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_RoundNumber" ON "QualifierRounds" ("FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor", "RoundNumber");
CREATE INDEX "IX_QualifierStandings_FromSeasonId_ToSeasonId" ON "QualifierStandings" ("FromSeasonId", "ToSeasonId");
CREATE INDEX "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor" ON "QualifierStandings" ("FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor");
CREATE UNIQUE INDEX "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_QualifierRank" ON "QualifierStandings" ("FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor", "QualifierRank");
CREATE UNIQUE INDEX "IX_QualifierStandings_FromSeasonId_ToSeasonId_QualifierBoundary_QualifierSportingColor_SaveAthleteId" ON "QualifierStandings" ("FromSeasonId", "ToSeasonId", "QualifierBoundary", "QualifierSportingColor", "SaveAthleteId");
CREATE INDEX "IX_QualifierStandings_SaveAthleteId" ON "QualifierStandings" ("SaveAthleteId");
CREATE INDEX "IX_Rounds_SeasonId_LeagueId_StageNumber" ON "Rounds" ("SeasonId", "LeagueId", "StageNumber");
CREATE UNIQUE INDEX "IX_Rounds_SeasonId_LeagueId_StageNumber_RoundNumber" ON "Rounds" ("SeasonId", "LeagueId", "StageNumber", "RoundNumber");
CREATE INDEX "IX_Rounds_StageId" ON "Rounds" ("StageId");
CREATE UNIQUE INDEX "IX_SaveAthletes_Name" ON "SaveAthletes" ("Name");
CREATE INDEX "IX_SaveAthletes_SportingColor" ON "SaveAthletes" ("SportingColor");
CREATE INDEX "IX_SeasonMemberships_SaveAthleteId" ON "SeasonMemberships" ("SaveAthleteId");
CREATE INDEX "IX_SeasonMemberships_SeasonId_LeagueId" ON "SeasonMemberships" ("SeasonId", "LeagueId");
CREATE INDEX "IX_SeasonMemberships_SeasonId_LeagueId_DrawIndex" ON "SeasonMemberships" ("SeasonId", "LeagueId", "DrawIndex");
CREATE UNIQUE INDEX "IX_SeasonMemberships_SeasonId_SaveAthleteId" ON "SeasonMemberships" ("SeasonId", "SaveAthleteId");
CREATE INDEX "IX_SeasonMemberships_SeasonId_SportingColor" ON "SeasonMemberships" ("SeasonId", "SportingColor");
CREATE INDEX "IX_SeasonStandings_SaveAthleteId" ON "SeasonStandings" ("SaveAthleteId");
CREATE INDEX "IX_SeasonStandings_SeasonId_LeagueId" ON "SeasonStandings" ("SeasonId", "LeagueId");
CREATE UNIQUE INDEX "IX_SeasonStandings_SeasonId_LeagueId_SaveAthleteId" ON "SeasonStandings" ("SeasonId", "LeagueId", "SaveAthleteId");
CREATE UNIQUE INDEX "IX_SeasonStandings_SeasonId_LeagueId_SeasonRank" ON "SeasonStandings" ("SeasonId", "LeagueId", "SeasonRank");
CREATE UNIQUE INDEX "IX_Seasons_SeasonNumber" ON "Seasons" ("SeasonNumber");
CREATE INDEX "IX_StageStandings_SaveAthleteId_SeasonId" ON "StageStandings" ("SaveAthleteId", "SeasonId");
CREATE INDEX "IX_StageStandings_SeasonId_LeagueId_StageNumber" ON "StageStandings" ("SeasonId", "LeagueId", "StageNumber");
CREATE UNIQUE INDEX "IX_StageStandings_SeasonId_LeagueId_StageNumber_SaveAthleteId" ON "StageStandings" ("SeasonId", "LeagueId", "StageNumber", "SaveAthleteId");
CREATE INDEX "IX_StageStandings_StageId" ON "StageStandings" ("StageId");
CREATE INDEX "IX_Stages_SeasonId_LeagueId" ON "Stages" ("SeasonId", "LeagueId");
CREATE UNIQUE INDEX "IX_Stages_SeasonId_LeagueId_StageNumber" ON "Stages" ("SeasonId", "LeagueId", "StageNumber");
CREATE UNIQUE INDEX "IX_StoryEvents_SaveAthleteId_EventType_DedupKey" ON "StoryEvents" ("SaveAthleteId", "EventType", "DedupKey");
CREATE INDEX "IX_StoryEvents_SaveAthleteId_Id" ON "StoryEvents" ("SaveAthleteId", "Id");
CREATE INDEX "IX_StoryEvents_SeasonNumber_Id" ON "StoryEvents" ("SeasonNumber", "Id");
CREATE INDEX "IX_TypeCupSelections_SaveAthleteId" ON "TypeCupSelections" ("SaveAthleteId");
CREATE INDEX "IX_TypeCupSelections_SourceSeasonId" ON "TypeCupSelections" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_TypeCupSelections_SourceSeasonId_CreatureType_SaveAthleteId" ON "TypeCupSelections" ("SourceSeasonId", "CreatureType", "SaveAthleteId");
CREATE UNIQUE INDEX "IX_TypeCupSelections_SourceSeasonId_CreatureType_SelectionRank" ON "TypeCupSelections" ("SourceSeasonId", "CreatureType", "SelectionRank");
CREATE UNIQUE INDEX "IX_TypeCupSelections_SourceSeasonId_SaveAthleteId" ON "TypeCupSelections" ("SourceSeasonId", "SaveAthleteId");
CREATE INDEX "IX_TypeCupTeamGroupStandings_SaveAthleteId" ON "TypeCupTeamGroupStandings" ("SaveAthleteId");
CREATE INDEX "IX_TypeCupTeamGroupStandings_SourceSeasonId" ON "TypeCupTeamGroupStandings" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_TypeCupTeamGroupStandings_SourceSeasonId_TournamentPhase_QualificationGroup_GroupNumber_GroupRank" ON "TypeCupTeamGroupStandings" ("SourceSeasonId", "TournamentPhase", "QualificationGroup", "GroupNumber", "GroupRank");
CREATE UNIQUE INDEX "IX_TypeCupTeamGroupStandings_SourceSeasonId_TournamentPhase_QualificationGroup_SaveAthleteId" ON "TypeCupTeamGroupStandings" ("SourceSeasonId", "TournamentPhase", "QualificationGroup", "SaveAthleteId");
CREATE INDEX "IX_TypeCupTeamRounds_SourceSeasonId" ON "TypeCupTeamRounds" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_TypeCupTeamRounds_SourceSeasonId_TournamentPhase_QualificationGroup_GroupNumber_RoundNumber" ON "TypeCupTeamRounds" ("SourceSeasonId", "TournamentPhase", "QualificationGroup", "GroupNumber", "RoundNumber");
CREATE INDEX "IX_TypeCupTeamStandings_SourceSeasonId" ON "TypeCupTeamStandings" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_TypeCupTeamStandings_SourceSeasonId_TournamentPhase_QualificationGroup_CreatureType" ON "TypeCupTeamStandings" ("SourceSeasonId", "TournamentPhase", "QualificationGroup", "CreatureType");
CREATE UNIQUE INDEX "IX_TypeCupTeamStandings_SourceSeasonId_TournamentPhase_QualificationGroup_TeamRank" ON "TypeCupTeamStandings" ("SourceSeasonId", "TournamentPhase", "QualificationGroup", "TeamRank");
CREATE INDEX "IX_TypeCupTournamentDraws_SourceSeasonId" ON "TypeCupTournamentDraws" ("SourceSeasonId");
CREATE UNIQUE INDEX "IX_TypeCupTournamentDraws_SourceSeasonId_CreatureType" ON "TypeCupTournamentDraws" ("SourceSeasonId", "CreatureType");
CREATE INDEX "IX_TypeCupTournamentDraws_SourceSeasonId_QualificationGroup" ON "TypeCupTournamentDraws" ("SourceSeasonId", "QualificationGroup");
