import { useEffect, useMemo, useRef, useState } from 'react';
import { Card } from '../../shared/ui/Card';
import { Loading, Notice } from '../../shared/ui/Notice';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { AthleteLink, Link } from '../routing/router';
import { athletePath, dashboardPath } from '../routing/routes';
import {
  fetchAthleteSearch,
  fetchAthleteSearchOptions,
  type AthleteSearchOptions,
  type AthleteSearchResponse,
} from './athleteSearchApi';
import {
  DEFAULT_ATHLETE_SEARCH,
  activeAthleteFilterChips,
  buildAthleteSearchString,
  isDefaultAthleteSearch,
  parseAthleteSearchParams,
  toAthleteSearchRequest,
  type AthleteSearchParams,
} from './athleteSearchParams';

function NumberField({
  label,
  value,
  min,
  max,
  placeholder,
  onChange,
}: {
  label: string;
  value: number | null;
  min: number;
  max: number;
  placeholder: string;
  onChange: (value: number | null) => void;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      <input
        type="number"
        min={min}
        max={max}
        placeholder={placeholder}
        value={value === null ? '' : String(value)}
        onChange={(event) => {
          const raw = event.target.value.trim();
          if (raw === '') {
            onChange(null);
            return;
          }
          const parsed = Number.parseInt(raw, 10);
          onChange(Number.isInteger(parsed) ? parsed : null);
        }}
      />
    </label>
  );
}

function ToggleList({
  legend,
  options,
  selected,
  getKey,
  getLabel,
  getHint,
  onToggle,
}: {
  legend: string;
  options: { key: string; label: string; hint?: string }[];
  selected: string[];
  getKey: (option: { key: string }) => string;
  getLabel: (option: { key: string; label: string }) => string;
  getHint?: (option: { key: string; label: string; hint?: string }) => string | undefined;
  onToggle: (key: string) => void;
}) {
  return (
    <fieldset className="check-group">
      <legend>{legend}</legend>
      {options.map((option) => {
        const key = getKey(option);
        const checked = selected.some((entry) => entry.toLowerCase() === key.toLowerCase());
        return (
          <label key={key} className="check-row">
            <input type="checkbox" checked={checked} onChange={() => onToggle(key)} />
            {getLabel(option)}
            {getHint ? (
              <span className="card-sub"> · {getHint(option) ?? ''}</span>
            ) : null}
          </label>
        );
      })}
    </fieldset>
  );
}

/**
 * Dedicated athlete search/browse page. Historical aggregation and filtering
 * happen in the backend/query layer; the browser only renders one page of
 * tailored search-result DTOs. Filter/search/sort state lives in the URL so
 * reloading or copying the link reproduces the same search.
 */
export function AthleteSearchPage({
  saveId,
  urlSearch,
  onSearchChange,
}: {
  saveId: string;
  urlSearch: string;
  onSearchChange: (search: string) => void;
}) {
  const [params, setParams] = useState<AthleteSearchParams>(() => parseAthleteSearchParams(urlSearch));
  const [options, setOptions] = useState<AthleteSearchOptions | null>(null);
  const [optionsError, setOptionsError] = useState<string | null>(null);
  const [response, setResponse] = useState<AthleteSearchResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const debounceRef = useRef<number | null>(null);

  useEffect(() => {
    setParams(parseAthleteSearchParams(urlSearch));
  }, [urlSearch, saveId]);

  useEffect(() => {
    const next = buildAthleteSearchString(params);
    if (next === urlSearch) {
      return;
    }
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
    }
    const immediate = params.q.trim() === parseAthleteSearchParams(urlSearch).q.trim();
    if (immediate) {
      onSearchChange(next);
      return;
    }
    debounceRef.current = window.setTimeout(() => {
      onSearchChange(next);
    }, 300);
    return () => {
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
      }
    };
  }, [params, urlSearch, onSearchChange]);

  useEffect(() => {
    setOptions(null);
    setOptionsError(null);
    const controller = new AbortController();
    fetchAthleteSearchOptions(saveId, controller.signal)
      .then((loaded) => {
        setOptions(loaded);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        setOptionsError(apiErrorMessage(failure));
      });
    return () => {
      controller.abort();
    };
  }, [saveId]);

  const requestKey = useMemo(() => buildAthleteSearchString(params), [params]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchAthleteSearch(saveId, toAthleteSearchRequest(params), controller.signal)
      .then((loaded) => {
        setResponse(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setResponse(null);
          setError(null);
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });
    return () => {
      controller.abort();
    };
  }, [saveId, requestKey]); // eslint-disable-line react-hooks/exhaustive-deps

  function update(next: AthleteSearchParams, resetPage = true): void {
    setParams({ ...next, skip: resetPage ? 0 : next.skip });
  }

  function removeChip(key: string): void {
    switch (key) {
      case 'q':
        update({ ...params, q: '' });
        break;
      case 'nonPool':
        update({ ...params, minNonPool: null, maxNonPool: null });
        break;
      case 'current':
        update({ ...params, current: [] });
        break;
      case 'colours':
        update({ ...params, colours: [] });
        break;
      case 'types':
        update({ ...params, types: [] });
        break;
      case 'honours':
        update({ ...params, minHonours: null, maxHonours: null });
        break;
      case 'minTitles':
        update({ ...params, minTitles: null });
        break;
      case 'hasTitle':
        update({ ...params, hasTitle: '' });
        break;
      case 'highest':
        update({ ...params, highest: '' });
        break;
      case 'bestFinishMax':
        update({ ...params, bestFinishMax: null });
        break;
      case 'superSeasons':
        update({ ...params, minSuperSeasons: null, maxSuperSeasons: null });
        break;
      case 'cup':
        update({ ...params, cup: '' });
        break;
      default:
        break;
    }
  }

  const chips = activeAthleteFilterChips(params);
  const isDefault = isDefaultAthleteSearch({ ...params, take: 100, skip: 0 });
  const results = response?.results ?? [];
  const totalCount = response?.totalCount ?? 0;
  const pageStart = totalCount === 0 ? 0 : params.skip + 1;
  const pageEnd = Math.min(params.skip + params.take, totalCount);
  const hasPrev = params.skip > 0;
  const hasNext = params.skip + params.take < totalCount;
  const colourOptions = (options?.colours ?? []).map((row) => ({
    key: String(row.value),
    label: `${row.name}`,
    hint: `${row.count}`,
  }));
  const leagueOptions = (options?.currentLeagues ?? []).map((row) => ({
    key: row.name,
    label: row.name,
    hint: `${row.count}`,
  }));
  const typeSuggestions = options?.creatureTypes ?? [];

  return (
    <div className="dashboard">
      <div className="toolbar" role="search" aria-label="Athlete search">
        <label className="field field-grow">
          <span>Search athletes by name</span>
          <input
            type="search"
            placeholder="Partial name, e.g. faerie…"
            value={params.q}
            onChange={(event) => {
              setParams({ ...params, q: event.target.value, skip: 0 });
            }}
          />
        </label>
        <label className="field">
          <span>Sort by</span>
          <select
            value={params.sort}
            onChange={(event) => {
              const sort = event.target.value as AthleteSearchParams['sort'];
              setParams({ ...params, sort, skip: 0 });
            }}
          >
            <option value="name">Name</option>
            <option value="nonPool">Non-pool seasons</option>
            <option value="honours">Honours</option>
            <option value="titles">Titles</option>
            <option value="bestFinish">Best league finish</option>
            <option value="currentLeague">Current league</option>
            <option value="superSeasons">Superleague seasons</option>
          </select>
        </label>
        <label className="field">
          <span>Direction</span>
          <select
            value={params.dir}
            onChange={(event) => {
              setParams({ ...params, dir: event.target.value === 'desc' ? 'desc' : 'asc', skip: 0 });
            }}
          >
            <option value="asc">Ascending</option>
            <option value="desc">Descending</option>
          </select>
        </label>
        <div className="toolbar-end">
          <span className="muted small" role="status">
            {loading && results.length === 0 ? 'Searching…' : `${totalCount} athlete${totalCount === 1 ? '' : 's'}`}
          </span>
          {!isDefault ? (
            <button
              type="button"
              className="ghost-button"
              onClick={() => {
                setParams(DEFAULT_ATHLETE_SEARCH);
                onSearchChange('');
              }}
            >
              Clear filters
            </button>
          ) : null}
        </div>
      </div>

      {chips.length > 0 ? (
        <p className="muted small" aria-live="polite">
          Active filters:{' '}
          {chips.map((chip) => (
            <span key={chip.key} className="badge badge-wait" style={{ marginRight: '0.35rem' }}>
              {chip.label}{' '}
              <button
                type="button"
                aria-label={`Remove filter ${chip.label}`}
                onClick={() => removeChip(chip.key)}
                style={{ background: 'none', border: 'none', cursor: 'pointer', color: 'inherit' }}
              >
                ×
              </button>
            </span>
          ))}
        </p>
      ) : null}

      <div className="page-grid">
        <Card
          eyebrow="Filters"
          title="Sporting filters"
          info={
            <p>
              Historical filters come from persisted history in the backend: non-pool seasons
              count distinct seasons outside the pool (0 or 1 per season, kept after pool
              returns), honours count league and Cup podiums, titles count first places only.
              Filters combine with AND across categories; multiple colours, types or leagues
              combine with OR.
            </p>
          }
        >
          <fieldset className="check-group">
            <legend>Non-pool seasons (prominent)</legend>
            <div className="live-controls">
              <NumberField
                label="At least"
                value={params.minNonPool}
                min={0}
                max={99}
                placeholder="e.g. 5"
                onChange={(value) => update({ ...params, minNonPool: value })}
              />
              <NumberField
                label="At most"
                value={params.maxNonPool}
                min={0}
                max={99}
                placeholder="No max"
                onChange={(value) => update({ ...params, maxNonPool: value })}
              />
            </div>
            {options ? (
              <p className="muted small">
                Highest in this save: {options.maxNonPoolSeasons} season(s).
              </p>
            ) : null}
          </fieldset>

          <ToggleList
            legend="Current league or pool"
            options={leagueOptions}
            selected={params.current}
            getKey={(option) => option.key}
            getLabel={(option) => option.label}
            getHint={(option) => option.hint}
            onToggle={(key) => {
              const exists = params.current.some((entry) => entry.toLowerCase() === key.toLowerCase());
              update({
                ...params,
                current: exists
                  ? params.current.filter((entry) => entry.toLowerCase() !== key.toLowerCase())
                  : [...params.current, key],
              });
            }}
          />
          {optionsError ? <p className="muted small">League options: {optionsError}</p> : null}

          <ToggleList
            legend="Sporting colour"
            options={colourOptions}
            selected={params.colours.map(String)}
            getKey={(option) => option.key}
            getLabel={(option) => {
              const match = options?.colours.find((row) => String(row.value) === option.key);
              return match ? match.name : option.label;
            }}
            getHint={(option) => option.hint}
            onToggle={(key) => {
              const value = Number.parseInt(key, 10);
              update({
                ...params,
                colours: params.colours.includes(value)
                  ? params.colours.filter((entry) => entry !== value)
                  : [...params.colours, value],
              });
            }}
          />

          <fieldset className="check-group">
            <legend>Creature types</legend>
            <label className="field">
              <span>Comma-separated, e.g. Faerie, Wizard</span>
              <input
                type="text"
                list="athlete-search-types"
                placeholder="Any type"
                value={params.types.join(', ')}
                onChange={(event) => {
                  const entries = event.target.value
                    .split(',')
                    .map((part) => part.trim())
                    .filter((part) => part.length > 0);
                  update({ ...params, types: [...new Set(entries)] });
                }}
              />
            </label>
            <datalist id="athlete-search-types">
              {typeSuggestions.map((row) => (
                <option key={row.value} value={row.value} />
              ))}
            </datalist>
          </fieldset>

          <fieldset className="check-group">
            <legend>Honours and titles</legend>
            <div className="live-controls">
              <NumberField
                label="Honours at least"
                value={params.minHonours}
                min={0}
                max={999}
                placeholder="e.g. 1"
                onChange={(value) => update({ ...params, minHonours: value })}
              />
              <NumberField
                label="Honours at most"
                value={params.maxHonours}
                min={0}
                max={999}
                placeholder="No max"
                onChange={(value) => update({ ...params, maxHonours: value })}
              />
            </div>
            <div className="live-controls">
              <NumberField
                label="Titles at least"
                value={params.minTitles}
                min={0}
                max={999}
                placeholder="e.g. 1"
                onChange={(value) => update({ ...params, minTitles: value })}
              />
              <label className="field">
                <span>Titles presence</span>
                <select
                  value={params.hasTitle}
                  onChange={(event) => {
                    const value = event.target.value;
                    update({ ...params, hasTitle: value === 'only' || value === 'none' ? value : '' });
                  }}
                >
                  <option value="">Any</option>
                  <option value="only">With a title</option>
                  <option value="none">Podiums only (no titles)</option>
                </select>
              </label>
            </div>
          </fieldset>

          <fieldset className="check-group">
            <legend>League history</legend>
            <label className="field">
              <span>Highest league reached</span>
              <select
                value={params.highest}
                onChange={(event) => {
                  const value = event.target.value;
                  update({
                    ...params,
                    highest: value === 'superleague' || value === 'feeder' || value === 'none' ? value : '',
                  });
                }}
              >
                <option value="">Any</option>
                <option value="superleague">Superleague</option>
                <option value="feeder">Feeder only</option>
                <option value="none">Never active</option>
              </select>
            </label>
            <div className="live-controls">
              <NumberField
                label="Best finish at most (P≤N)"
                value={params.bestFinishMax}
                min={1}
                max={32}
                placeholder="e.g. 3"
                onChange={(value) => update({ ...params, bestFinishMax: value })}
              />
            </div>
            <div className="live-controls">
              <NumberField
                label="Superleague seasons at least"
                value={params.minSuperSeasons}
                min={0}
                max={99}
                placeholder="e.g. 1"
                onChange={(value) => update({ ...params, minSuperSeasons: value })}
              />
              <NumberField
                label="Superleague seasons at most"
                value={params.maxSuperSeasons}
                min={0}
                max={99}
                placeholder="No max"
                onChange={(value) => update({ ...params, maxSuperSeasons: value })}
              />
            </div>
          </fieldset>

          <fieldset className="check-group">
            <legend>Cup participation</legend>
            <label className="field">
              <span>Cup status</span>
              <select
                value={params.cup}
                onChange={(event) => {
                  const value = event.target.value;
                  update({
                    ...params,
                    cup:
                      value === 'participant' || value === 'podium' || value === 'title' || value === 'none'
                        ? value
                        : '',
                  });
                }}
              >
                <option value="">Any</option>
                <option value="participant">Cup participant</option>
                <option value="podium">Cup podium</option>
                <option value="title">Cup title</option>
                <option value="none">No Cup appearances</option>
              </select>
            </label>
          </fieldset>
        </Card>

        <Card
          eyebrow="Results"
          title={totalCount === 0 ? 'No athletes found' : `${pageStart}–${pageEnd} of ${totalCount}`}
          action={
            <span className="live-buttons">
              <button type="button" className="ghost-button" disabled={!hasPrev} onClick={() => setParams({ ...params, skip: Math.max(0, params.skip - params.take) })}>
                Previous
              </button>{' '}
              <button
                type="button"
                className="ghost-button"
                disabled={!hasNext}
                onClick={() => setParams({ ...params, skip: params.skip + params.take })}
              >
                Next
              </button>
            </span>
          }
          info={
            <p>
              Each row links to the existing athlete profile. Sorting and pagination apply after
              all filters; the URL always reflects the current search.
            </p>
          }
        >
          {loading && results.length === 0 ? <Loading label="Searching athletes…" /> : null}
          {error ? (
            <Notice tone="error" title="Search unavailable">
              <p>{error}</p>
            </Notice>
          ) : null}
          {!loading && !error && totalCount === 0 ? (
            <Notice tone="empty" title={params.q.trim() && chips.length === 1 ? `No athletes match “${params.q.trim()}”` : 'No athletes match the current filters'}>
              <p>
                {params.q.trim() && chips.length === 1
                  ? 'Try a shorter name fragment; search is case-insensitive and matches partial names.'
                  : 'Adjust the filters or clear them to browse the full roster.'}
              </p>
              <p className="live-buttons">
                <button
                  type="button"
                  className="ghost-button"
                  onClick={() => {
                    setParams(DEFAULT_ATHLETE_SEARCH);
                    onSearchChange('');
                  }}
                >
                  Clear filters
                </button>{' '}
                <Link to={dashboardPath(saveId)} className="ghost-button">
                  Back to dashboard
                </Link>
              </p>
            </Notice>
          ) : null}
          {results.length > 0 ? (
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Card</th>
                    <th scope="col">Status</th>
                    <th scope="col">Non-pool</th>
                    <th scope="col">Honours</th>
                    <th scope="col">Titles</th>
                    <th scope="col">Best</th>
                    <th scope="col">Cup</th>
                  </tr>
                </thead>
                <tbody>
                  {results.map((row) => (
                    <tr key={row.athleteId}>
                      <td>
                        <div className="card-cell">
                          {row.imageUrl ? (
                            <img className="card-thumb" src={row.imageUrl} alt="" loading="lazy" />
                          ) : (
                            <span className="card-thumb card-thumb-fallback" aria-hidden="true">
                              {row.name.slice(0, 2).toUpperCase()}
                            </span>
                          )}
                          <span className="card-identity">
                            <AthleteLink saveId={saveId} athleteId={row.athleteId} name={row.name} />
                            <span className="card-sub">
                              {row.sportingColorName} · {(row.creatureTypes ?? []).join(', ') || 'Unknown type'}
                            </span>
                          </span>
                        </div>
                      </td>
                      <td>{row.isActive ? (row.currentLeagueName ?? 'Active') : 'Common pool'}</td>
                      <td className="numeric">{row.nonPoolSeasons}</td>
                      <td className="numeric">{row.honoursCount}</td>
                      <td className="numeric">{row.titlesCount}</td>
                      <td className="numeric">
                        {row.bestSeasonFinish !== null ? `P${row.bestSeasonFinish}` : '—'}
                      </td>
                      <td className="numeric">
                        {row.cupTitles > 0 ? `${row.cupTitles} title${row.cupTitles === 1 ? '' : 's'}` : row.cupPodiums > 0 ? `${row.cupPodiums} podium${row.cupPodiums === 1 ? '' : 's'}` : row.cupAppearances > 0 ? `${row.cupAppearances} app` : '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
          {loading && results.length > 0 ? <p className="muted small">Refreshing…</p> : null}
          <p className="muted small">
            Profile links open <code>{athletePath(saveId, 0).replace('/0', '/:athleteId')}</code> in
            this save; multi-tab profile navigation is unchanged.
          </p>
        </Card>
      </div>
    </div>
  );
}
