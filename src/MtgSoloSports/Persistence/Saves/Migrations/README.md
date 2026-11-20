# Save schema migrations

This is the authoritative policy for the save database schema (each save is
one SQLite file under the saves root). Schema migration and game-rule
migration are separate concepts: nothing here may touch `RulesSnapshots`
content, RNG state, or any sporting result. Rules compatibility is enforced
before/after every schema migration by
[`SaveRulesCompatibility`](../SaveRulesCompatibility.cs).

## Policy: toolchain-truthful artifacts

Every schema artifact must stay exactly in sync with the real
`SaveDbContext` model, and drift must fail loudly rather than silently
generate broken SQL. Concretely:

1. **Every migration carries a `.Designer.cs` companion** whose
   `BuildTargetModel` is the model as of that migration, with the
   `[DbContext]`/`[Migration]` attributes living in the Designer (never
   duplicated in the main migration file). `dotnet ef migrations remove`,
   idempotent scripts, and future scaffolding all depend on these target
   models.
2. **`SaveDbContextModelSnapshot.cs` is toolchain-generated** (the EF Core
   10 generator, equivalent to `dotnet ef`) and must match the model at
   HEAD. `dotnet ef migrations add` diffs against this snapshot, so any
   drift would silently be absorbed into the next migration.
3. **New saves are created at the baseline**, not by replaying the
   historical chain: [`SaveSchemaBaseline`](../SaveSchemaBaseline.cs)
   applies the committed squashed DDL
   ([`SaveSchemaBaseline.sql`](../SaveSchemaBaseline.sql)) in one batched
   command and stamps the covered `__EFMigrationsHistory` rows, so fresh
   saves and fully migrated saves are indistinguishable. Existing saves
   never use the baseline; they keep migrating through the full chain via
   `SaveStore.EnsureMigratedAsync` -> `SaveSchemaMigrator`
   (checkpoint -> migrate -> verify -> rollback on failure).

### Guards (ordinary tests, so CI fails loudly)

- `SaveMigrationPolicyTests.EveryDefinedMigration_HasADesignerCompanionWithItsTargetModel`
  fails when a migration without a Designer companion is added.
- `SaveMigrationPolicyTests.ModelSnapshot_MatchesTheRealModelSoTheNextScaffoldedMigrationIsExact`
  fails when the snapshot drifts from the model, using the same
  differ pipeline `dotnet ef migrations add` uses.
- `SaveSchemaBaselineTests.BaselineCreatedSchema_IsExactlyTheFullChainReplaySchema`
  fails when the baseline DDL is not byte-equivalent to replaying the full
  chain (tables, columns in chain order, indexes, checks, and EF
  bookkeeping tables compared through `sqlite_master`).
- `SaveSchemaBaseline.ApplyAsync` itself fails loudly at save creation when
  the application defines migrations newer than the baseline, so a stale
  baseline can never produce a save whose schema lies about its history.

## How to add a schema migration

1. Change `SaveDbContext` (and entities) as usual.
2. Scaffold the migration with the EF Core toolchain, for example:

   ```bash
   dotnet tool install --global dotnet-ef          # once per machine
   dotnet ef migrations add <Name> --project src/MtgSoloSports --context SaveDbContext
   ```

   Review the generated `Up`/`Down`; hand edits are allowed, but the
   migration and its Designer must stay truthful to the model as of the
   migration.
3. Regenerate the new-save baseline (one command, deliberately explicit):

   ```bash
   MTGSS_REGEN_SAVE_SCHEMA_BASELINE=1 dotnet test MtgSoloSports.slnx --configuration Release \
       --filter "FullyQualifiedName~SaveSchemaBaselineTests.RegenerateBaseline"
   ```

   This replays the full chain once into a temp database and rewrites
   `src/MtgSoloSports/Persistence/Saves/SaveSchemaBaseline.sql` (tables
   first, then indexes, each statement terminated with `;`, first line the
   chain marker `-- save-schema-baseline-through: <lastMigrationId>`).
4. Run the backend suite. The guards above fail with pointers to this file
   if any artifact is out of sync.

## Retroactively adding a Designer to a hand-written migration

The Designer target model is the model at the migration's introducing
commit, so reconstruct it from that commit (not from HEAD): check out the
commit, generate the Designer with the same EF Core generator (`dotnet ef
migrations add` scaffold at that commit, or the equivalent
`CSharpMigrationsGenerator.GenerateMetadata` call), and keep only the
Designer content, renamed to the migration's id and class. Then confirm the
model at that commit contains the migration's own changes and none of any
later migration's changes. This is how the nine pre-MSS-149 hand-written
migrations got their Designers.
