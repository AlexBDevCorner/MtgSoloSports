import { fetchJson } from '../../shared/api/http';

export interface StoryEventItem {
  id: number;
  eventType: string;
  athleteId: number;
  athleteName: string;
  seasonNumber: number;
  stageNumber: number | null;
  contextJson: string;
  text: string;
}

export interface StoryFeed {
  saveId: string;
  stories: StoryEventItem[];
}

export async function fetchRecentStories(
  saveId: string,
  take = 20,
  signal?: AbortSignal,
): Promise<StoryFeed> {
  return fetchJson<StoryFeed>(
    `/api/saves/${saveId}/stories/recent?take=${take}`,
    { signal },
  );
}

export async function fetchAthleteStories(
  saveId: string,
  athleteId: number,
  take = 20,
  signal?: AbortSignal,
): Promise<StoryFeed> {
  return fetchJson<StoryFeed>(
    `/api/saves/${saveId}/athletes/${athleteId}/stories?take=${take}`,
    { signal },
  );
}
