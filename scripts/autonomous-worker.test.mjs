import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const workflow = readFileSync(new URL('../.github/workflows/autonomous-worker.yml', import.meta.url), 'utf8');
const recovery = workflow.split('      - name: Preserve worker progress and ensure pull request\n')[1]
  .split('        run: |\n')[1].replace(/^          /gm, '');

// Execute the actual recovery step with GitHub and git writes replaced by local stubs.
function recover(commits, outcome = 'success', mode = 'implementation') {
  return spawnSync('bash', ['-c', `
    git() {
      case "$1 $2" in
        'remote set-url'|'fetch origin') return 0 ;;
        'branch --show-current') echo "autonomous/$TASK_ID" ;;
        'status --porcelain') return 0 ;;
        'rev-list --count') echo "$TEST_COMMITS" ;;
        'push -u') echo 'published branch' ;;
        *) echo "Unexpected git command: $1 $2" >&2; return 99 ;;
      esac
    }
    gh() {
      case "$1 $2" in
        'auth setup-git') return 0 ;;
        'pr list') echo 42 ;;
        'pr view') echo false ;;
        *) echo "Unexpected gh command: $1 $2" >&2; return 99 ;;
      esac
    }
    ${recovery}
  `], {
    encoding: 'utf8',
    env: { ...process.env, TASK_ID: 'MSS-148', GH_REPO: 'Owner/Repo', BASE_BRANCH: 'main',
      WORKER_OUTCOME: outcome, WORK_MODE: mode, TEST_COMMITS: String(commits) },
  });
}

test('empty worker output fails recovery and never publishes', () => {
  for (const outcome of ['success', 'failure']) {
    const result = recover(0, outcome);
    assert.ifError(result.error);
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, /Worker produced no task commits/);
    assert.doesNotMatch(result.stdout, /published branch/);
  }
});

test('existing task commits still publish for implementation and correction', () => {
  for (const mode of ['implementation', 'correction']) {
    const result = recover(1, 'success', mode);
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /published branch/);
    assert.match(result.stdout, /Published successful worker output to PR #42/);
  }
});
