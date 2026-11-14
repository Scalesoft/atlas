/** Persisted reference and display snapshot. Authentication never enters the document. */
export interface YouTrackIssue {
  readonly id: string
  readonly key: string
  readonly title: string
  readonly url: string
  /** Optional display snapshot, not a live authorization or synchronization source. */
  readonly state?: string | null
}

/** The host owns the YouTrack API, credentials, tenancy and authorization. */
export interface YouTrackClient {
  search(query: string, context: { readonly signal: AbortSignal }): Promise<readonly YouTrackIssue[]>
  /** Resolve an ID or pasted URL through the host's current authorization boundary. */
  resolve?(reference: string, context: { readonly signal: AbortSignal }): Promise<YouTrackIssue | null>
}

export function validateIssueText(value: unknown): asserts value is string {
  if (typeof value !== 'string' || !value.trim() || value.length > 2000 || /[\u0000-\u001f\u007f]/.test(value)) {
    throw new TypeError('YouTrack issue fields must be non-empty, single-line strings.')
  }
}

export function validateIssueUrl(value: unknown): asserts value is string {
  validateIssueText(value)
  let url: URL
  try { url = new URL(value) } catch { throw new TypeError('YouTrack issue URL must be absolute.') }
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) {
    throw new TypeError('YouTrack issue URL must use HTTP(S) without embedded credentials.')
  }
}

export function normalizeYouTrackIssue(value: unknown): YouTrackIssue {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError('Invalid YouTrack issue.')
  const issue = value as Record<string, unknown>
  validateIssueText(issue.id)
  validateIssueText(issue.key)
  validateIssueText(issue.title)
  validateIssueUrl(issue.url)
  if (issue.state != null) validateIssueText(issue.state)
  return { id: issue.id.trim(), key: issue.key.trim(), title: issue.title.trim(), url: new URL(issue.url).href,
    ...(issue.state == null ? {} : { state: (issue.state as string).trim() }) }
}
