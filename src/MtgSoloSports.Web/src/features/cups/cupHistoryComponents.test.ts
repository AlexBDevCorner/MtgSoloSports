import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

const badge = read('TeamBadge.tsx');
const tiles = read('SquadTiles.tsx');
const podium = read('CupPodium.tsx');
const css = read('CupHistory.css');
const tokens = readFileSync(join(here, '..', '..', 'shared', 'ui', 'tokens.css'), 'utf8');

describe('cup history components', () => {
  it('links a team badge to its team page and offers a non-link mark for use inside links', () => {
    assert.ok(badge.includes('cupTeamPath(saveId, cup, teamKey)'));
    assert.ok(badge.includes('<Link'));
    assert.ok(badge.includes('export function TeamMark'));
    assert.ok(badge.includes('teamSwatchClass(cup, teamKey)'));
  });

  it('shows a lettered fallback instead of a broken image when art is missing', () => {
    assert.ok(tiles.includes('imageUrl ?'), 'art is conditional');
    assert.ok(tiles.includes('card-art-fallback'));
    assert.ok(tiles.includes('initials(name)'));
    assert.ok(tiles.includes('loading="lazy"'));
  });

  it('links every squad member to its athlete profile', () => {
    assert.ok(tiles.includes('<AthleteLink'));
    assert.ok(!tiles.includes('window.open'));
  });

  it('renders the podium as an ordered list in place order', () => {
    assert.ok(podium.includes('<ol className="cup-podium"'));
    assert.ok(podium.includes('a.place - b.place'));
  });

  it('defines a swatch for every Color team from tokens', () => {
    for (const key of ['white', 'blue', 'black', 'red', 'green', 'multicolor', 'hybrid', 'colorless']) {
      assert.ok(css.includes(`.team-swatch-${key}`), `swatch for ${key}`);
      assert.ok(css.includes(`var(--team-${key})`), `token use for ${key}`);
      assert.match(tokens, new RegExp(`--team-${key}:`), `token for ${key}`);
    }
  });
});
