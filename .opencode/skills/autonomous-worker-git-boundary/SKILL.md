---
name: autonomous-worker-git-boundary
description: Mandatory git and GitHub boundary for AutonomousWork-dispatched OpenCode sessions. Load before any git or gh operation so implementation stays local and the post-worker recovery step remains the only normal publisher.
---

# Autonomous worker git boundary

The model session implements and verifies code. It does **not** publish normal task work to GitHub.

## Allowed local git work

- Inspect with `git status`, `git diff`, `git log`, `git show`, and similar read-only commands.
- Stage intended task files and create meaningful local commits.
- Reuse the workflow-prepared `autonomous/<TASK-ID>` checkout. Do not create a separate publication branch.
- Finish with intended task changes committed and the working tree clean, except for the workflow-managed read-only `control/` checkout.

## Forbidden remote mutation during the model session

Do not run or cause:

- `git push` in any form, including `--force`, `--force-with-lease`, tags, or explicit refspecs.
- `git remote set-url`, remote credential rewrites, or attempts to bypass the workflow's disabled push URL.
- `gh pr create`, `gh pr edit`, `gh pr ready`, `gh pr reopen`, `gh pr merge`, or equivalent GitHub API mutations.
- Remote branch/ref creation, deletion, or updates.
- Credential discovery/recovery such as `gh auth token`, printing token environment variables, reading credential helpers, or inspecting GitHub authorization headers.

A failed push or authentication attempt is not a reason to recover credentials or try another transport. Stop that remote action and continue local work.

## GitHub inspection

Normal `gh` usage must be read-only: inspect PRs, checks, runs, logs, files, and review state without changing them.

The one exception is **correction mode**: when a trusted blocking review is genuinely technically wrong, the workflow protocol may require one machine-readable disagreement comment. Follow that exact protocol and make no other GitHub mutation.

## Publication ownership

After the model process exits, the workflow mints a fresh GitHub App token. The post-worker recovery step owns pushing the committed HEAD to the canonical `autonomous/<TASK-ID>` branch and creating/reusing/readying the PR.

Do not duplicate that responsibility inside the agent session.
