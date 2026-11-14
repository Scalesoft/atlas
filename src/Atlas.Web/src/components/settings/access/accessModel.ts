/**
 * The access contract of the security centre (docs/access-model.md §3, `/api/access/*`). Every access in Atlas is one
 * sentence: who · what · may do · where it comes from · until when. The server returns that sentence for every kind of
 * resource; the person's card and the resource's panel render the same entries with AccessEntryRow. JSON is camelCase,
 * dates are ISO strings.
 */

/** One staircase for all kinds: Vidí (10) → Čte (20) → Upravuje (30) → Spravuje (40). null means "nemá přístup". */
export type AccessLevel = "discover" | "read" | "write" | "manage";

/** A kind of resource the server knows (GET /api/access/kinds): core kinds and those of enabled plugins. */
export interface AccessKindView {
  /** "wiki" | "space" | "page" | "vault" | "secret" | "token" | "youtrack" | plugin ids. */
  id: string;
  one: string;
  many: string;
  /** Atlas icon name. */
  icon: string;
  /** "space" for page/vault/youtrack, "vault" for secret. */
  parent: string | null;
  /** "YouTrack" for plugin kinds. */
  plugin: string | null;
  /** Lowest first, each with what it allows here. */
  levels: { level: AccessLevel; description: string }[];
  /** Has a writer and the caller may grant something of this kind. */
  grantable: boolean;
}

/** One path by which access reaches a subject. */
export interface AccessSource {
  via: "direct" | "group" | "automatic" | "derived";
  /** Group name, "Správce wiki", "Správce prostoru Provoz", "Z role v prostoru". */
  label: string;
  /** The level this path gives. */
  level: AccessLevel;
  groupId?: string | null;
  groupSource?: "hub" | "local" | null;
  grantId?: string | null;
  /** CAS version of the grant (ActiveFrom), for a change or revoke. */
  version?: string | null;
  startAt?: string | null;
  expiresAt?: string | null;
  /** The authorization deadline is a renewable Hub verification, not the grant's end. */
  verificationLimited?: boolean;
  grantExpiresAt?: string | null;
  /** False when outside its validity window ("Ještě neplatí" / "Platnost skončila"). */
  effective: boolean;
}

/** Person or group card: one entry per reachable resource. */
export interface AccessEntry {
  kind: string;
  /** For wiki: "wiki". */
  resourceId: string;
  /** "Servery a síť"; for kind space the UI says "Celý prostor". */
  title: string;
  /** secret → vault name; page → parent path "Architektura". */
  parentTitle?: string | null;
  spaceId?: string | null;
  spaceName?: string | null;
  spaceIcon?: string | null;
  spaceColor?: string | null;
  /** Effective (highest effective source); null = none applies now (a page with own access, a token that can no longer authenticate). */
  level: AccessLevel | null;
  /** Strongest first. */
  sources: AccessSource[];
  /** "4 hesla", "41 stránek přebírá přístup prostoru", "Vlastní přístup — nepřebírá běžná práva prostoru", "2 projekty". */
  note?: string | null;
}

export interface AccessPrincipal {
  kind: "user" | "group";
  id: string;
  name: string;
  /** Username, or "12 členů". */
  detail?: string | null;
  isActive?: boolean;
  isWikiAdmin?: boolean;
  /** null on a space-scoped card (principals?spaceId=). */
  lastSignInAt?: string | null;
  /** null on a space-scoped card (principals?spaceId=). */
  groups?: { id: string; name: string }[] | null;
}

export interface PrincipalAccess {
  principal: AccessPrincipal;
  scope: "wiki" | "space" | "self";
  entries: AccessEntry[];
  /** The caller may change the grants shown here. */
  canManage: boolean;
}

export interface AccessSubject {
  kind: "user" | "group";
  id: string;
  name: string;
  detail?: string | null;
  memberCount?: number | null;
  isActive?: boolean;
}

/** Who has access to one resource: one row per person or group. */
export interface ResourceAccessEntry {
  subject: AccessSubject;
  /** null when none of the subject's sources applies now (a grant not started or expired, an inactive account); the grant can still be changed or revoked. */
  level: AccessLevel | null;
  sources: AccessSource[];
}

/** What inside a space differs from the whole space. */
export interface AccessException {
  kind: "page" | "vault" | "secret";
  id: string;
  title: string;
  parentTitle?: string | null;
  mode: "own" | "grants";
  grantCount: number;
}

export interface ResourceAccess {
  resource: { kind: string; id: string; title: string; spaceId?: string | null; spaceName?: string | null; parentTitle?: string | null; note?: string | null };
  canManage: boolean;
  /** "Správci wiki", "Správci prostoru": locked rows. */
  automatic: { label: string; detail: string; level: AccessLevel; count: number }[];
  /** view=direct: only grants set here; view=effective: also people through groups. */
  entries: ResourceAccessEntry[];
  hasMore: boolean;
  /** Kind space only. */
  exceptions?: AccessException[];
}

/** A resource the caller may grant, for the "K čemu" step (GET /api/access/resources?kind=&q=). */
export interface AccessResource {
  kind: string;
  id: string;
  title: string;
  parentTitle?: string | null;
  spaceId?: string | null;
  spaceName?: string | null;
  note?: string | null;
}

export type ResourceAccessView = "effective" | "direct";

/** A person or group a manager may grant access to (GET /api/access/candidates, q of one character at least, at most 20). */
export interface AccessCandidate {
  kind: "user" | "group";
  id: string;
  name: string;
  /** The username, or for a group its active member count as text. */
  detail: string | null;
}

/** PUT /api/access/resources/{kind}/{id}/grants. */
export interface AccessGrantBody {
  subjectKind: "user" | "group";
  subjectId: string;
  level: AccessLevel;
  startAt: string | null;
  expiresAt: string | null;
  /** The current grant's version, null for a new grant. */
  expectedVersion: string | null;
}

/** GET /api/access/principals/user/{id}/revoke-all: what "Odebrat vše" would close. */
export interface RevokeAllImpact {
  grants: number;
  groups: number;
  tokens: number;
}

/** GET /api/admin/api-tokens: every personal token of the wiki. */
export interface AdminApiToken {
  id: string;
  userId: string;
  userName: string;
  name: string;
  scopes: string[];
  createdAt: string;
  expiresAt: string;
  /** Always null: token use is not recorded. */
  lastUsedAt: string | null;
  revokedAt: string | null;
  /** rest and/or mcp; absent from a server that predates channels (rest). */
  channels?: string[];
}

/** An inline change as the API takes it: a level on the kind's staircase and the validity (UTC instants or null). */
export interface AccessChangeDraft {
  level: AccessLevel;
  startAt: string | null;
  expiresAt: string | null;
}
