# Journalist archive fixture example (MSS-135) — NOT a real-save report

All files in this directory are small, hand-written **fixture illustrations**
for the workflow in `docs/journalist-workflow.md`. They use placeholder
identity (`saveId 11111111-1111-1111-1111-111111111111`, fingerprints
`example-only`) and only two athletes where real evidence always carries 32.

Do NOT present this content as reporting on a real save, do NOT commit it as
an archive entry, and do NOT point `--article`/`--evidence` at these files in
a real `archive commit`. Real commits save the `evidence` command stdout
verbatim plus your chat-written Markdown article.

Contents:

- `season-02-stage-01.example.md` — illustrative article shape (facts first,
  editorial clearly separated, evidence refs cited).
- `season-02-stage-01.example.meta.json` — illustrative provenance sidecar
  shape (archive/evidence versions, cutoff, fingerprints, revision).
- `season-02-notebook.example.md` — illustrative scoped notebook entry shape
  (dated `## Season N Stage M` header; unscoped sections stay out of
  historical context).
