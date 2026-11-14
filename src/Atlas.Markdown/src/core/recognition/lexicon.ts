/**
 * What a place (a space) knows by name, the shape of the server's `GET /api/spaces/{id}/lexicon`: only what the reader may read.
 * `truncated`: the server left entries out, so a single match may not be the only one: nothing becomes a chip by itself.
 */
export interface MarkdownLexicon {
  readonly version: string
  readonly truncated: boolean
  readonly people: readonly MarkdownLexiconPerson[]
  readonly pages: readonly MarkdownLexiconPage[]
  /** The YouTrack project keys („PX2“) an issue key may start with; empty when YouTrack is not set up. */
  readonly youTrackProjects: readonly string[]
}
export interface MarkdownLexiconPerson {
  /** The identity key (`object:<id>`, `user:<id>`…), unique in the lexicon. */
  readonly key: string
  readonly contactId?: string | null
  readonly userId?: string | null
  readonly name: string
  /** Learned aliases, nicknames and initials („MMu“, „Mára“): only ever offered (D4). */
  readonly aliases: readonly string[]
  readonly org?: string | null
  readonly role?: string | null
  readonly uses: number
}
export interface MarkdownLexiconPage { readonly id: string; readonly title: string }

/**
 * The host's lexicon for an editor (controller option `lexicon`): `read` answers from memory at once (undefined until it is in),
 * `subscribe` tells when it changed (a new version arrived); the editor never waits for it.
 */
export interface MarkdownLexiconSource {
  read(): MarkdownLexicon | undefined
  subscribe?(onChange: () => void): () => void
}
