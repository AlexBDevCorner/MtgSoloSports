import { defineConfig, globalIgnores } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

// MSS-156: a small curated web gate, mirroring the backend's "small curated
// analyzer set" philosophy. Scope is deliberately limited to TypeScript ESLint
// recommended plus the two React hooks rules from the React 19-compatible
// plugin line. No styling, ownership or compiler-era packs.
//
// Type-aware linting was measured and rejected: on this tree it costs ~17s
// (~3x the syntactic pass) and reports a ~1700-error backlog dominated by
// no-floating-promises (1034), no-unnecessary-type-assertion (374) and
// require-await (218). Paying that down is outside MSS-156's scope, so the
// gate runs syntactic rules only; switching to type-checked configs later
// means adding `projectService` and fixing that backlog first.
export default defineConfig(
  globalIgnores(['dist/']),
  ...tseslint.configs.recommended,
  {
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
    },
  },
  {
    // A suppression that no longer blocks a real finding fails the gate;
    // every eslint-disable must stay used and carry a safety justification.
    linterOptions: { reportUnusedDisableDirectives: 'error' },
  },
);
