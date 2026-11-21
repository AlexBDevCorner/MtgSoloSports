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

/** True when the failure is the DOMException a fetch rejects with on abort. */
export function isAbort(failure: unknown): boolean {
  return failure instanceof DOMException && failure.name === 'AbortError';
}

export function apiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    // Prefer the server's own detail (structured JSON message, else a short
    // raw body) so recoverable-error screens keep showing diagnostics.
    const parsed = tryParseErrorBody(error.body);
    if (parsed) {
      return parsed;
    }
    if (error.status >= 500) {
      // A server failure is never the user's request being wrong: a 500
      // without a usable body renders an actionable message instead of a
      // bare status line.
      return 'The server failed while processing this request. The save data may be damaged; check the server logs before continuing.';
    }
    return `Request failed (${error.status}).`;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'Something went wrong.';
}

function tryParseErrorBody(body: string): string | null {
  const structured = tryParseStructuredError(body);
  if (structured) {
    return structured;
  }
  return body.length > 0 && body.length < 300 ? body : null;
}

function tryParseStructuredError(body: string): string | null {
  try {
    const parsed = JSON.parse(body) as { error?: unknown };
    if (typeof parsed.error === 'string' && parsed.error.length > 0) {
      return parsed.error;
    }
    return null;
  } catch {
    return null;
  }
}
