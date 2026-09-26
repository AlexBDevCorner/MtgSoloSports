export async function fetchJson<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await fetch(input, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  });
  if (!response.ok) {
    const body = await response.text().catch(() => '');
    throw new ApiError(response.status, body || response.statusText);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

export class ApiError extends Error {
  readonly status: number;
  readonly body: string;

  constructor(status: number, body: string) {
    super(`Request failed with status ${status}: ${body}`);
    this.status = status;
    this.body = body;
  }
}

export function apiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const parsed = tryParseErrorBody(error.body);
    if (parsed) {
      return parsed;
    }
    return `Request failed (${error.status}).`;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'Something went wrong.';
}

function tryParseErrorBody(body: string): string | null {
  try {
    const parsed = JSON.parse(body) as { error?: unknown };
    if (typeof parsed.error === 'string' && parsed.error.length > 0) {
      return parsed.error;
    }
    return null;
  } catch {
    return body.length > 0 && body.length < 300 ? body : null;
  }
}
