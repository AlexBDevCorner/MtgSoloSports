# Autonomous Development Setup

MtgSoloSports is intended to participate in `AlexBDevCorner/AutonomousWork`, but autonomous execution starts **disabled**.

## Control-repo registration

Project:

```yaml
id: mtgsolosports
name: MtgSoloSports
repository: AlexBDevCorner/MtgSoloSports
enabled: false
max_active_tasks: 1
```

Tasks use prefix `MSS-*` and initially remain `draft`.

## Target workflow

`.github/workflows/autonomous-worker.yml` is adapted from the working RepoManager/MandarinBotNet model. It accepts only `MSS-*` tasks from `projects/mtgsolosports/tasks/` in `AutonomousWork`.

The workflow is intentionally unusable while the control project is disabled because the control guard validates project/task eligibility.

## GitHub App / repository configuration required before enabling

The repository needs the same autonomous GitHub App model used by the existing projects:

- repository variable `AUTONOMOUS_APP_CLIENT_ID`;
- repository secret `AUTONOMOUS_APP_PRIVATE_KEY`;
- repository secret `CONTROL_REPO_TOKEN` with read access to the private `AutonomousWork` control repo;
- repository secret `OPENCODE_API_KEY`;
- AutonomousWork GitHub App installed on `MtgSoloSports` with the permissions expected by the workflow (Contents, Pull requests, Issues and Workflows write; Actions and Checks read).

Do not commit any of these values.

## Activation checklist

1. GitHub repository exists as `AlexBDevCorner/MtgSoloSports`, default branch `main`.
2. This seed content is pushed.
3. Required secrets/variable and GitHub App installation are configured.
4. Normal CI is green.
5. Autonomous worker can be manually dispatched only after the control project/task guard is intentionally enabled.
6. Review/dispatcher/reconciler configuration recognizes the project.
7. Human changes desired tasks from `draft` to `ready`.
8. Human changes `projects/mtgsolosports/project.yaml` to `enabled: true`.

Until steps 7-8, no implementation task is authorized for execution.

## Review expectations

Review every autonomous PR against:

- task compliance;
- build/test evidence;
- vertical-slice locality;
- no MediatR/generic architecture layers;
- deterministic RNG usage;
- fixed-point sporting math;
- persistence transaction/RNG consistency;
- invariants/tests;
- unrelated changes;
- migration/save compatibility when applicable.
