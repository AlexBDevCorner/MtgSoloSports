using Microsoft.EntityFrameworkCore;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One <see cref="SaveDbContext"/> maps exactly one save SQLite file.
/// Feature slices may use this context directly; generic repositories and
/// UnitOfWork abstractions are forbidden.
/// </summary>
public sealed class SaveDbContext : DbContext
{
    public SaveDbContext(DbContextOptions<SaveDbContext> options)
        : base(options)
    {
    }

    public DbSet<SaveMetadataEntity> SaveMetadata => Set<SaveMetadataEntity>();

    public DbSet<RulesSnapshotEntity> RulesSnapshots => Set<RulesSnapshotEntity>();

    public DbSet<RngStateEntity> RngStates => Set<RngStateEntity>();

    public DbSet<SaveAthleteEntity> SaveAthletes => Set<SaveAthleteEntity>();

    public DbSet<SeasonEntity> Seasons => Set<SeasonEntity>();

    public DbSet<LeagueEntity> Leagues => Set<LeagueEntity>();

    public DbSet<SeasonMembershipEntity> SeasonMemberships => Set<SeasonMembershipEntity>();

    public DbSet<StageEntity> Stages => Set<StageEntity>();

    public DbSet<RoundEntity> Rounds => Set<RoundEntity>();

    public DbSet<StageStandingEntity> StageStandings => Set<StageStandingEntity>();

    public DbSet<SeasonStandingEntity> SeasonStandings => Set<SeasonStandingEntity>();

    public DbSet<AthleteCareerEntity> AthleteCareers => Set<AthleteCareerEntity>();

    public DbSet<AthleteSeasonSummaryEntity> AthleteSeasonSummaries => Set<AthleteSeasonSummaryEntity>();

    public DbSet<MovementEntity> Movements => Set<MovementEntity>();

    public DbSet<QualifierRoundEntity> QualifierRounds => Set<QualifierRoundEntity>();

    public DbSet<QualifierStandingEntity> QualifierStandings => Set<QualifierStandingEntity>();

    public DbSet<StoryEventEntity> StoryEvents => Set<StoryEventEntity>();

    public DbSet<HonourEntity> Honours => Set<HonourEntity>();

    public DbSet<ColorCupSelectionEntity> ColorCupSelections => Set<ColorCupSelectionEntity>();

    public DbSet<ColorCupIndividualRoundEntity> ColorCupIndividualRounds => Set<ColorCupIndividualRoundEntity>();

    public DbSet<ColorCupIndividualStandingEntity> ColorCupIndividualStandings => Set<ColorCupIndividualStandingEntity>();

    public DbSet<ColorCupTeamRoundEntity> ColorCupTeamRounds => Set<ColorCupTeamRoundEntity>();

    public DbSet<ColorCupTeamGroupStandingEntity> ColorCupTeamGroupStandings => Set<ColorCupTeamGroupStandingEntity>();

    public DbSet<ColorCupTeamStandingEntity> ColorCupTeamStandings => Set<ColorCupTeamStandingEntity>();

    public DbSet<TypeCupSelectionEntity> TypeCupSelections => Set<TypeCupSelectionEntity>();

    public DbSet<TypeCupTeamRoundEntity> TypeCupTeamRounds => Set<TypeCupTeamRoundEntity>();

    public DbSet<TypeCupTeamGroupStandingEntity> TypeCupTeamGroupStandings => Set<TypeCupTeamGroupStandingEntity>();

    public DbSet<TypeCupTeamStandingEntity> TypeCupTeamStandings => Set<TypeCupTeamStandingEntity>();

    public DbSet<CupSelectionReportEntity> CupSelectionReports => Set<CupSelectionReportEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureSingleRowTables(modelBuilder);
        ConfigureAthletes(modelBuilder);
        ConfigureSeasonTables(modelBuilder);
        ConfigureSimulationTables(modelBuilder);
        ConfigureAthleteProjections(modelBuilder);
        ConfigureMovements(modelBuilder);
        ConfigureQualifier(modelBuilder);
        ConfigureStoryEvents(modelBuilder);
        ConfigureHonours(modelBuilder);
        ConfigureColorCupSelections(modelBuilder);
        ConfigureColorCupIndividual(modelBuilder);
        ConfigureColorCupTeam(modelBuilder);
        ConfigureTypeCupSelections(modelBuilder);
        ConfigureTypeCupTeam(modelBuilder);
        ConfigureCupSelectionReports(modelBuilder);
    }

    private static void ConfigureCupSelectionReports(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CupSelectionReportEntity>(entity =>
        {
            entity.ToTable("CupSelectionReports");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.SourceSeasonId).IsUnique();
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.Cup).IsRequired().HasMaxLength(16);
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
        });
    }

    private static void ConfigureSingleRowTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SaveMetadataEntity>(entity =>
        {
            entity.ToTable("SaveMetadata", table => table.HasCheckConstraint("CK_SaveMetadata_SingleRow", "\"Id\" = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.SaveId).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedUtc).IsRequired();
            entity.Property(e => e.SchemaVersion).IsRequired();
            entity.Property(e => e.CurrentSeason).IsRequired();
            entity.Property(e => e.Phase).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<RulesSnapshotEntity>(entity =>
        {
            entity.ToTable("RulesSnapshots", table => table.HasCheckConstraint("CK_RulesSnapshots_SingleRow", "\"Id\" = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RulesJson).IsRequired();
        });

        modelBuilder.Entity<RngStateEntity>(entity =>
        {
            entity.ToTable("RngStates", table => table.HasCheckConstraint("CK_RngStates_SingleRow", "\"Id\" = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Algorithm).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AlgorithmVersion).IsRequired();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.Stream).IsRequired();
        });
    }

    private static void ConfigureAthletes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SaveAthleteEntity>(entity =>
        {
            entity.ToTable("SaveAthletes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.SportingColor);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.CreatureTypesJson).IsRequired();
            entity.Property(e => e.FrontColors).IsRequired().HasMaxLength(8);
            entity.Property(e => e.ManaCost).IsRequired();
            entity.Property(e => e.TypeLine).IsRequired();
            entity.Property(e => e.ImageUrl).HasMaxLength(1024);
            entity.Property(e => e.SetCode).HasMaxLength(16);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.TypeCupNationality).HasMaxLength(64);
        });
    }

    private static void ConfigureSeasonTables(ModelBuilder modelBuilder)
    {
        ConfigureSeasons(modelBuilder);
        ConfigureLeagues(modelBuilder);
        ConfigureMemberships(modelBuilder);
    }

    private static void ConfigureSeasons(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeasonEntity>(entity =>
        {
            entity.ToTable("Seasons");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.SeasonNumber).IsUnique();
            entity.Property(e => e.SeasonNumber).IsRequired();
            entity.Property(e => e.HasSuperleague).IsRequired();
            entity.Property(e => e.IsComplete).IsRequired();
        });
    }

    private static void ConfigureLeagues(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeagueEntity>(entity =>
        {
            entity.ToTable("Leagues");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.Kind, e.FeederDivision, e.SportingColor }).IsUnique();
            entity.HasIndex(e => e.SeasonId);
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.FeederDivision).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(64);
        });
    }

    private static void ConfigureMemberships(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeasonMembershipEntity>(entity =>
        {
            entity.ToTable("SeasonMemberships");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId });
            entity.HasIndex(e => new { e.SeasonId, e.SportingColor });
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.DrawIndex });
            // Long-run projection/record paths filter by athlete across seasons.
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.DrawIndex).IsRequired();
        });
    }

    private static void ConfigureSimulationTables(ModelBuilder modelBuilder)
    {
        ConfigureStages(modelBuilder);
        ConfigureStageStandings(modelBuilder);
        ConfigureSeasonStandings(modelBuilder);
        ConfigureRounds(modelBuilder);
    }

    private static void ConfigureStages(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StageEntity>(entity =>
        {
            entity.ToTable("Stages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.StageNumber }).IsUnique();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId });
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.LeagueId).IsRequired();
            entity.Property(e => e.StageNumber).IsRequired();
            entity.Property(e => e.CompletedRounds).IsRequired();
            entity.Property(e => e.IsComplete).IsRequired();
        });
    }

    private static void ConfigureStageStandings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StageStandingEntity>(entity =>
        {
            entity.ToTable("StageStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.StageNumber, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.StageNumber });
            entity.HasIndex(e => e.StageId);
            // Long-run bonus path filters by athlete plus a six-season window;
            // without this index every round/stage scans the full standings table.
            entity.HasIndex(e => new { e.SaveAthleteId, e.SeasonId });
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.LeagueId).IsRequired();
            entity.Property(e => e.StageId).IsRequired();
            entity.Property(e => e.StageNumber).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.StageRank).IsRequired();
            entity.Property(e => e.StageScoreThousandths).IsRequired();
            entity.Property(e => e.BaseScoreThousandths).IsRequired();
            entity.Property(e => e.ChampionshipPointsThousandths).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.EarnedBonusThousandths).IsRequired();
        });
    }

    private static void ConfigureSeasonStandings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeasonStandingEntity>(entity =>
        {
            entity.ToTable("SeasonStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.SeasonRank }).IsUnique();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId });
            // Long-run career/profile/record paths filter by athlete across seasons.
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.LeagueId).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.SeasonRank).IsRequired();
            entity.Property(e => e.TotalChampionshipPointsThousandths).IsRequired();
            entity.Property(e => e.TotalStageScoreThousandths).IsRequired();
            entity.Property(e => e.TotalBaseScoreThousandths).IsRequired();
            entity.Property(e => e.StageWins).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.StagePlaceCountsJson).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.IsChampion).IsRequired();
        });
    }

    private static void ConfigureAthleteProjections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AthleteCareerEntity>(entity =>
        {
            entity.ToTable("AthleteCareers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.SaveAthleteId).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.SeasonsActive).IsRequired();
            entity.Property(e => e.CurrentLeagueName).HasMaxLength(64);
            entity.Property(e => e.IsActive).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.StageWins).IsRequired();
            entity.Property(e => e.StageSeconds).IsRequired();
            entity.Property(e => e.StageThirds).IsRequired();
            entity.Property(e => e.LifetimeEarnedBonusThousandths).IsRequired();
            entity.Property(e => e.CurrentEffectiveBonusThousandths).IsRequired();
            entity.Property(e => e.LastSeasonNumber).IsRequired();
            entity.Property(e => e.LastStageNumber).IsRequired();
        });

        modelBuilder.Entity<AthleteSeasonSummaryEntity>(entity =>
        {
            entity.ToTable("AthleteSeasonSummaries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SaveAthleteId, e.SeasonNumber });
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId });
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.SeasonNumber).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.LeagueName).HasMaxLength(64);
            entity.Property(e => e.WasActive).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.StageWins).IsRequired();
            entity.Property(e => e.StageSeconds).IsRequired();
            entity.Property(e => e.StageThirds).IsRequired();
            entity.Property(e => e.IsChampion).IsRequired();
            entity.Property(e => e.EarnedBonusThousandths).IsRequired();
            entity.Property(e => e.TotalChampionshipPointsThousandths).IsRequired();
            entity.Property(e => e.TotalStageScoreThousandths).IsRequired();
            entity.Property(e => e.TotalBaseScoreThousandths).IsRequired();
        });
    }

    private static void ConfigureRounds(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoundEntity>(entity =>
        {
            entity.ToTable("Rounds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.StageNumber, e.RoundNumber }).IsUnique();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.StageNumber });
            entity.HasIndex(e => e.StageId);
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.LeagueId).IsRequired();
            entity.Property(e => e.StageId).IsRequired();
            entity.Property(e => e.StageNumber).IsRequired();
            entity.Property(e => e.RoundNumber).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RngBeforeState).IsRequired();
            entity.Property(e => e.RngBeforeStream).IsRequired();
            entity.Property(e => e.RngAfterState).IsRequired();
            entity.Property(e => e.RngAfterStream).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.PayloadChecksum).IsRequired().HasMaxLength(64);
        });
    }

    private static void ConfigureMovements(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MovementEntity>(entity =>
        {
            entity.ToTable("Movements");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.ToSeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.FromSeasonId, e.FromLeagueId });
            entity.HasIndex(e => new { e.ToSeasonId, e.ToLeagueId });
            // Athlete profile path filters by athlete and orders by destination
            // season/kind; without an athlete-leading index every profile scans
            // the full movements table on long saves.
            entity.HasIndex(e => new { e.SaveAthleteId, e.ToSeasonId, e.Kind });
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.FromSeasonId).IsRequired();
            entity.Property(e => e.ToSeasonId).IsRequired();
            entity.Property(e => e.FromLeagueId).IsRequired();
            entity.Property(e => e.ToLeagueId).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.FromSeasonRank).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
        });
    }

    private static void ConfigureQualifier(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QualifierRoundEntity>(entity =>
        {
            entity.ToTable("QualifierRounds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.FromSeasonId, e.ToSeasonId, e.RoundNumber }).IsUnique();
            entity.HasIndex(e => new { e.FromSeasonId, e.ToSeasonId });
            entity.Property(e => e.FromSeasonId).IsRequired();
            entity.Property(e => e.ToSeasonId).IsRequired();
            entity.Property(e => e.RoundNumber).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RngBeforeState).IsRequired();
            entity.Property(e => e.RngBeforeStream).IsRequired();
            entity.Property(e => e.RngAfterState).IsRequired();
            entity.Property(e => e.RngAfterStream).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.PayloadChecksum).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<QualifierStandingEntity>(entity =>
        {
            entity.ToTable("QualifierStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.FromSeasonId, e.ToSeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.FromSeasonId, e.ToSeasonId, e.QualifierRank }).IsUnique();
            entity.HasIndex(e => new { e.FromSeasonId, e.ToSeasonId });
            entity.Property(e => e.FromSeasonId).IsRequired();
            entity.Property(e => e.ToSeasonId).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.QualifierRank).IsRequired();
            entity.Property(e => e.QualifierScoreThousandths).IsRequired();
            entity.Property(e => e.BaseScoreThousandths).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.IsQualified).IsRequired();
            entity.Property(e => e.Role).IsRequired();
            entity.Property(e => e.FromLeagueId).IsRequired();
            entity.Property(e => e.FromSeasonRank).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
        });
    }

    private static void ConfigureStoryEvents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoryEventEntity>(entity =>
        {
            entity.ToTable("StoryEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SaveAthleteId, e.EventType, e.DedupKey }).IsUnique();
            // Newest-N athlete feed filters by athlete and orders by Id desc;
            // the composite lets SQLite satisfy filter plus order from the index
            // without a separate sort. It covers the old single-column athlete
            // prefix, so no separate SaveAthleteId index is kept.
            entity.HasIndex(e => new { e.SaveAthleteId, e.Id });
            entity.HasIndex(e => new { e.SeasonNumber, e.Id });
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.DedupKey).IsRequired().HasMaxLength(128);
            entity.Property(e => e.SeasonNumber).IsRequired();
            entity.Property(e => e.ContextJson).IsRequired();
        });
    }

    private static void ConfigureHonours(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HonourEntity>(entity =>
        {
            entity.ToTable("Honours");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SeasonId, e.LeagueId, e.SaveAthleteId, e.Kind }).IsUnique();
            entity.HasIndex(e => new { e.SaveAthleteId, e.Kind });
            entity.HasIndex(e => e.SeasonNumber);
            entity.Property(e => e.SeasonId).IsRequired();
            entity.Property(e => e.SeasonNumber).IsRequired();
            entity.Property(e => e.LeagueId).IsRequired();
            entity.Property(e => e.LeagueName).IsRequired().HasMaxLength(64);
            entity.Property(e => e.LeagueKind).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
        });
    }

    private static void ConfigureColorCupSelections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ColorCupSelectionEntity>(entity =>
        {
            entity.ToTable("ColorCupSelections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.SportingColor, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.SportingColor, e.SelectionRank }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.SelectionRank).IsRequired();
            entity.Property(e => e.FinalRatingThousandths).IsRequired();
            entity.Property(e => e.BonusNormThousandths).IsRequired();
            entity.Property(e => e.PerformanceNormThousandths).IsRequired();
            entity.Property(e => e.FormNormThousandths).IsRequired();
            entity.Property(e => e.PrestigeNormThousandths).IsRequired();
            entity.Property(e => e.BonusRawThousandths).IsRequired();
            entity.Property(e => e.PerformanceRawThousandths).IsRequired();
            entity.Property(e => e.FormRaw).IsRequired();
            entity.Property(e => e.PrestigeRaw).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
        });
    }

    private static void ConfigureColorCupIndividual(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ColorCupIndividualRoundEntity>(entity =>
        {
            entity.ToTable("ColorCupIndividualRounds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.RoundNumber }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.RoundNumber).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RngBeforeState).IsRequired();
            entity.Property(e => e.RngBeforeStream).IsRequired();
            entity.Property(e => e.RngAfterState).IsRequired();
            entity.Property(e => e.RngAfterStream).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.PayloadChecksum).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<ColorCupIndividualStandingEntity>(entity =>
        {
            entity.ToTable("ColorCupIndividualStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.CupRank }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.CupRank).IsRequired();
            entity.Property(e => e.CupScoreThousandths).IsRequired();
            entity.Property(e => e.BaseScoreThousandths).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.Medal).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.SelectionRank).IsRequired();
        });
    }

    private static void ConfigureColorCupTeam(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureTeamRounds(modelBuilder);
        ConfigureTeamGroupStandings(modelBuilder);
        ConfigureTeamStandings(modelBuilder);
    }

    private static void ConfigureTeamRounds(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ColorCupTeamRoundEntity>(entity =>
        {
            entity.ToTable("ColorCupTeamRounds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.GroupNumber, e.RoundNumber }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.GroupNumber).IsRequired();
            entity.Property(e => e.RoundNumber).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RngBeforeState).IsRequired();
            entity.Property(e => e.RngBeforeStream).IsRequired();
            entity.Property(e => e.RngAfterState).IsRequired();
            entity.Property(e => e.RngAfterStream).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.PayloadChecksum).IsRequired().HasMaxLength(64);
        });
    }

    private static void ConfigureTeamGroupStandings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ColorCupTeamGroupStandingEntity>(entity =>
        {
            entity.ToTable("ColorCupTeamGroupStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.GroupNumber, e.GroupRank }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.GroupNumber).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.GroupRank).IsRequired();
            entity.Property(e => e.GroupScoreThousandths).IsRequired();
            entity.Property(e => e.BaseScoreThousandths).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.SelectionRank).IsRequired();
        });
    }

    private static void ConfigureTeamStandings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ColorCupTeamStandingEntity>(entity =>
        {
            entity.ToTable("ColorCupTeamStandings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SourceSeasonId, e.SportingColor }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.TeamRank }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.TeamRank).IsRequired();
            entity.Property(e => e.TeamScoreThousandths).IsRequired();
            entity.Property(e => e.TeamBaseThousandths).IsRequired();
            entity.Property(e => e.GroupWins).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.GroupPlaceCountsJson).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.Medal).IsRequired();
        });
    }

    private static void ConfigureTypeCupSelections(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<TypeCupSelectionEntity>(entity =>
        {
            entity.ToTable("TypeCupSelections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.CreatureType, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.CreatureType, e.SelectionRank }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.CreatureType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.SelectionRank).IsRequired();
            entity.Property(e => e.TypeRank).IsRequired();
            entity.Property(e => e.FinalRatingThousandths).IsRequired();
            entity.Property(e => e.BonusNormThousandths).IsRequired();
            entity.Property(e => e.PerformanceNormThousandths).IsRequired();
            entity.Property(e => e.FormNormThousandths).IsRequired();
            entity.Property(e => e.PrestigeNormThousandths).IsRequired();
            entity.Property(e => e.BonusRawThousandths).IsRequired();
            entity.Property(e => e.PerformanceRawThousandths).IsRequired();
            entity.Property(e => e.FormRaw).IsRequired();
            entity.Property(e => e.PrestigeRaw).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
        });
    }

    private static void ConfigureTypeCupTeam(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureTypeCupTeamRounds(modelBuilder);
        ConfigureTypeCupTeamGroupStandings(modelBuilder);
        ConfigureTypeCupTeamStandings(modelBuilder);
    }

    private static void ConfigureTypeCupTeamRounds(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TypeCupTeamRoundEntity>(entity =>
        {
            entity.ToTable("TypeCupTeamRounds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.GroupNumber, e.RoundNumber }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.GroupNumber).IsRequired();
            entity.Property(e => e.RoundNumber).IsRequired();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RngBeforeState).IsRequired();
            entity.Property(e => e.RngBeforeStream).IsRequired();
            entity.Property(e => e.RngAfterState).IsRequired();
            entity.Property(e => e.RngAfterStream).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.PayloadChecksum).IsRequired().HasMaxLength(64);
        });
    }

    private static void ConfigureTypeCupTeamGroupStandings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TypeCupTeamGroupStandingEntity>(entity =>
        {
            entity.ToTable("TypeCupTeamGroupStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.SaveAthleteId }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.GroupNumber, e.GroupRank }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.HasIndex(e => e.SaveAthleteId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.GroupNumber).IsRequired();
            entity.Property(e => e.SaveAthleteId).IsRequired();
            entity.Property(e => e.GroupRank).IsRequired();
            entity.Property(e => e.GroupScoreThousandths).IsRequired();
            entity.Property(e => e.BaseScoreThousandths).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.CreatureType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SelectionRank).IsRequired();
        });
    }

    private static void ConfigureTypeCupTeamStandings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TypeCupTeamStandingEntity>(entity =>
        {
            entity.ToTable("TypeCupTeamStandings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.SourceSeasonId, e.CreatureType }).IsUnique();
            entity.HasIndex(e => new { e.SourceSeasonId, e.TeamRank }).IsUnique();
            entity.HasIndex(e => e.SourceSeasonId);
            entity.Property(e => e.SourceSeasonId).IsRequired();
            entity.Property(e => e.SourceSeasonNumber).IsRequired();
            entity.Property(e => e.CreatureType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TeamRank).IsRequired();
            entity.Property(e => e.TeamScoreThousandths).IsRequired();
            entity.Property(e => e.TeamBaseThousandths).IsRequired();
            entity.Property(e => e.GroupWins).IsRequired();
            entity.Property(e => e.RoundWins).IsRequired();
            entity.Property(e => e.GroupPlaceCountsJson).IsRequired();
            entity.Property(e => e.RoundPlaceCountsJson).IsRequired();
            entity.Property(e => e.Medal).IsRequired();
        });
    }

    /// <summary>
    /// Stages an RNG state update on the tracked single-row entity.
    /// Callers commit it with <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// in the same transaction as generated simulation results.
    /// </summary>
    public void ApplyRngState(Pcg32State next)
    {
        RngStateEntity entity = RngStates.Local.SingleOrDefault(e => e.Id == 1)
            ?? throw new InvalidOperationException("RNG state row is not tracked. Load it before applying a new state.");
        entity.Algorithm = Pcg32V1.AlgorithmName;
        entity.AlgorithmVersion = Pcg32V1.AlgorithmVersion;
        entity.State = unchecked((long)next.State);
        entity.Stream = unchecked((long)next.Stream);
    }
}
