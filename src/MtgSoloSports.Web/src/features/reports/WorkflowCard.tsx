import { useState } from 'react';
import { Card } from '../../shared/ui/Card';

const JOURNALIST_PROMPT = `You are a longtime MtgSoloSports columnist. Use only facts from the attached season dossier (current season) and universe almanac (history). Distinguish official points (season championship points) from weighted career points; Type Cup qualification never awards titles; Color individual and team events are separate titles; provisional tables are not final. Call out defending champions, streaks, upsets, qualification cut lines, and historical firsts with season numbers. If a fact is not in the sources, say it is unavailable rather than guessing.`;

/** ChatGPT workflow card with the copyable example journalist prompt. */
export function WorkflowCard() {
  const [copied, setCopied] = useState(false);

  async function handleCopyPrompt(): Promise<void> {
    try {
      await navigator.clipboard.writeText(JOURNALIST_PROMPT);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  }

  return (
    <Card eyebrow="Workflow" title="How to brief ChatGPT">
      <ol className="muted">
        <li>Export the almanac occasionally as a stable history reference.</li>
        <li>Export a fresh season dossier whenever you want coverage of a new stage or Cup.</li>
        <li>Attach both files to a ChatGPT Project or a new chat; replace the dossier as the save advances.</li>
      </ol>
      <p className="muted small">
        Reports provide facts and evidence, not prewritten opinions. Scoring note: official points are season
        totals in the dossier tables; weighted career points appear only in the almanac leaders and never
        decide a season title.
      </p>
      <label className="field">
        <span>Example journalist prompt (copy into ChatGPT; no narrative is generated in-app)</span>
        <pre className="muted small" style={{ whiteSpace: 'pre-wrap' }}>
          {JOURNALIST_PROMPT}
        </pre>
      </label>
      <p>
        <button type="button" className="ghost-button" onClick={() => void handleCopyPrompt()}>
          {copied ? 'Copied!' : 'Copy prompt'}
        </button>
      </p>
    </Card>
  );
}
