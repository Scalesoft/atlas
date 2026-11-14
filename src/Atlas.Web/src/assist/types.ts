import type { MarkdownCommand, MarkdownPlugin } from "@atlas/markdown/core";
import type { ContactDirectoryService } from "@atlas/markdown/plugins/contacts";
import type { MarkdownPageSource } from "@atlas/markdown/plugins/pages";
import type { YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import type { LinkCandidateFetch } from "./linkSuggestions";
import type { SecretExamples } from "./rules";

/** What the host picker answers: the reference to write (built by the secrets host) and where the secret lives. */
export interface PickedSecret {
  readonly command: MarkdownCommand;
  readonly inline: boolean;
  readonly vaultName?: string;
}

/**
 * `pick`: „Vložit odkaz na heslo z trezoru…“ (the ordinary picker, card or inline). `replace`: „Přesunout do trezoru“ —
 * the author stores the password in KeePass and picks it; the span becomes an inline reference (plan §12 H.5, O2).
 * The picker never receives the span: `initialQuery` is built from the words around it.
 */
export interface SecretPickRequest {
  readonly mode: "pick" | "replace";
  readonly initialQuery: string;
  readonly signal: AbortSignal;
}

/**
 * Everything the `atlasAssist` adapter needs from the page editor (DocumentEditor), one per live editor session.
 * All lookups use the reader's own authorization (the same endpoints as the editor's pickers); nothing here sends a
 * block, a span with a secret or a value anywhere.
 */
export interface AtlasAssistContext {
  /** Off still allows explicit transforms; model hints are on-only. Both getters are fresh at every call. */
  readonly automaticSuggestionsEnabled?: () => boolean;
  readonly modelSuggestionsEnabled?: () => boolean;
  readonly writing?: {
    readonly transformAllowed: () => boolean;
    readonly outlineAllowed: () => boolean;
    readonly intentAllowed: () => boolean;
    readonly title: () => string;
    readonly onApplied: () => void;
    readonly api?: import('./writingApi').AtlasWritingApi;
  };
  /**
   * P11 Zeptat se / Vysvětlit / Shrnout over what the author points at (aiIntents.ts): `available` is the one rule (the assistant can
   * answer now), `run` hands the target's text (within the limit, without a secret) to the assistant panel.
   */
  readonly ask?: {
    readonly available: () => boolean;
    readonly run: (intent: import("./aiIntents").ReadingIntent, text: string, target: string) => unknown;
  };
  /** Local gallery adapter may observe anonymously without issuing backend calls. */
  readonly reportInteraction?: typeof import("../ai/interactions").reportAiInteraction;
  readonly pageId: () => string | undefined;
  /** People for contacts (editorDirectories.ts `createContactDirectory`). */
  readonly contacts: ContactDirectoryService;
  /** Pages for links (editorDirectories.ts `createPageSource`, batched reference check). */
  readonly pages: MarkdownPageSource;
  /** YouTrack `resolve` of the space; `null` for a missing or invisible issue (404). */
  readonly resolveIssue: (key: string, context: { readonly signal: AbortSignal }) => Promise<YouTrackIssue | null>;
  /** Whether the reader may Discover a secret matching the query (GET /api/secrets/search). */
  readonly hasSecrets: (query: string, signal: AbortSignal) => Promise<boolean>;
  /** Opens the host SecretPicker; `null` when the author closed it. */
  readonly pickSecret: (request: SecretPickRequest) => Promise<PickedSecret | null>;
  /** A short confirmation after a suggestion was applied (useToast); `undo` puts „Zpět“ on it (P6). */
  readonly notify: (message: string, undo?: () => void) => void;
  /** A notice that something did not happen (useToast error), e.g. a suggestion whose span changed meanwhile. */
  readonly warn: (message: string) => void;
  /** Reduced motion beyond the system setting (the gallery's switch). */
  readonly reducedMotion?: () => boolean;
  /** „Je to jen příklad“: defaults to the session memory shared with the publication check. */
  readonly examples?: SecretExamples;
  /** E2: link candidates of a finished paragraph (POST /api/assist/link-candidates); the gallery passes local data. */
  readonly linkCandidates?: LinkCandidateFetch;
  /** Profil → AI: whether the author wants this suggestion type (`link-mention`, …); every type when absent. */
  readonly suggestionTypeEnabled?: (type: string) => boolean;
  /** Capability `editorLinks` (GET /api/ai/capabilities): pages close in meaning only with it; the server decides when absent. */
  readonly editorLinks?: () => boolean;
  /**
   * E4: while its editor is attached, the adapter hands the host a reader of the section around the caret
   * (sectionContext.ts) for ordering the `[[` list; the returned function withdraws that reader.
   */
  readonly registerSection?: (read: () => string | null) => () => void;
}

/** The lazily loaded adapter, bound to the editor module: one assist plugin per live editor. */
export type AtlasAssistFactory = ((context: AtlasAssistContext) => MarkdownPlugin) & {
  readonly automaticSuggestionsEnabled?: () => boolean;
  readonly modelSuggestionsEnabled?: () => boolean;
};
