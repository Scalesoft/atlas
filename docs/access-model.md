# Access model and the security centre

Owner-approved design, 2026-09-29. The prototype is in the component gallery (`?gallery=1` → "Bezpečnostní centrum (návrh)", `components/dev/SecurityCenterExample.vue`) and was accepted in this state: level colours, the level column along the right edge, and row actions that appear on hover over the row's right end.

## 1. One sentence, one staircase

Every access in Atlas is one sentence: **who** · **what** · **may do** · **where it comes from** · **until when**. The server returns this sentence for every kind of resource. The UI always draws it with the same row (`AccessEntryRow`).

The level is one staircase for all kinds. It is written in the API as lowercase strings with a rank:

| Level | API | Rank | Label |
|---|---|---|---|
| Discover | `discover` | 10 | Vidí |
| Read | `read` | 20 | Čte |
| Write | `write` | 30 | Upravuje |
| Manage | `manage` | 40 | Spravuje |

Each kind uses only the levels that mean something for it and describes them in its own words:

- `space`: read/write/manage. These are today's `SpaceRole` values Reader/Writer/Admin; storage is unchanged.
- `page`: read/write/manage. These are today's `AccessRole` values DocumentReader/Writer/Manager.
- `vault` and `secret`: discover/read/manage. These are today's `VaultAccessLevel` values; `write` stays reserved.
- `token`: read/write. These are the personal API token scopes.
- `wiki`: manage. This is `IsWikiAdmin`.
- `youtrack` (plugin, read-only, derived): read. YouTrack projects allowed for a space are readable by everyone with at least Read in that space.

**Storage does not change.** `SpaceGrant`, `ResourceGrant`, `PersonalApiToken` and `UserAccount.IsWikiAdmin` stay as they are. The access kinds are a read and write layer over them.

UI wording follows the staircase everywhere, including space and page: the old "Čtenář / Autor / Správce" labels become "Čte / Upravuje / Spravuje".

## 2. Server: access kinds

Feature folder `Features/Access` (`Contracts`, `Queries`, `Commands`, `Domain`), with Wolverine handlers and the existing `IMessageBus` endpoint style.

```csharp
public interface IAccessKind
{
    AccessKindDescriptor Descriptor { get; }
    // Everything `who` can reach in this kind, as entries (one per resource, effective level, all sources).
    Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct);
    // Who can reach one resource of this kind. Returns null when the kind does not list its resources this way.
    Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct);
    // Resources of this kind the caller may grant, for the "K čemu" step. Empty when not grantable.
    Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct);
    // Null for read-only / derived kinds.
    IAccessGrantWriter? Writer { get; }
}
```

- Each kind is registered in its feature's `AddXxx()` as `services.AddScoped<IAccessKind, SpaceAccessKind>()`. The access feature collects `IEnumerable<IAccessKind>`.
- A plugin registers its own kind the same way, from its own module. Nothing in `Features/Access` names a specific plugin.
- Every kind reuses the existing evaluators and snapshots: `SpaceRoles`, `ResourceGrantSnapshots` / `ResourceAccessPolicy`, `VaultGrantSnapshots`, `PageAccessDirectory` / `VaultAccessDirectory` SQL, and `GrantQueryLimits`. Kinds do not invent new rules. Tests prove parity with today's authorization.
- Writers delegate to the existing commands: `SetSpaceGrant`, `SetPageGrant`, `SetVaultGrant` / `RevokeVaultGrant`, `SetSecretGrant`. Level strings map to the stored role values. The existing endpoints keep working.

### Security guard (owner decision 3a)

`ISecurityChangeGuard` (fresh sign-in, with the 10-minute session window that already applies to security changes) is now required for:

- granting or changing to **manage** on a space (`space.grant` with role Admin) or on a page (`page.grant` with Manager);
- every vault and secret grant change (as today);
- turning **WikiAdmin** on or off for an account (`account.wiki_admin`);
- `revoke-all`.

Lower space and page grants and revokes need no fresh sign-in. The checks go into the existing handlers, so the old and the new endpoints enforce them the same way.

## 3. HTTP contract

JSON is camelCase. Dates are ISO strings. `level` is one of `discover|read|write|manage`, or `null` for "nemá přístup".

```ts
type AccessLevel = "discover" | "read" | "write" | "manage";

interface AccessKindView {
  id: string;            // "wiki" | "space" | "page" | "vault" | "secret" | "token" | "youtrack" | plugin ids
  one: string;           // "Trezor"
  many: string;          // "Trezory"
  icon: string;          // Atlas icon name
  parent: string | null; // "space" for page/vault/youtrack, "vault" for secret
  plugin: string | null; // "YouTrack" for plugin kinds
  levels: { level: AccessLevel; description: string }[]; // lowest first
  grantable: boolean;    // has a Writer and the caller may grant something of this kind
}

interface AccessSource {
  via: "direct" | "group" | "automatic" | "derived";
  label: string;          // group name, "Správce wiki", "Správce prostoru Provoz", "Z role v prostoru"
  level: AccessLevel;     // level this path gives
  groupId?: string | null;
  grantId?: string | null;
  version?: string | null; // CAS version of the grant (ActiveFrom), for change/revoke
  startAt?: string | null;
  expiresAt?: string | null; // the authorization deadline of this path (the earlier of the grant's end and the Hub lease)
  verificationLimited?: boolean; // expiresAt is a renewable Hub verification, not the grant's end (display only)
  grantExpiresAt?: string | null; // the real end of the grant of a Hub group path; null = unlimited
  groupSource?: "hub" | "local" | null;
  effective: boolean;     // false when outside its validity window (shown as "Ještě neplatí" / "Platnost skončila")
}

// Person / group card: one entry per reachable resource.
interface AccessEntry {
  kind: string;
  resourceId: string;       // for wiki: "wiki"
  title: string;            // "Servery a síť", "Celý prostor" is decided by the UI for kind space
  parentTitle?: string | null;  // secret → vault name; page → parent path "Architektura"
  spaceId?: string | null;
  spaceName?: string | null;
  spaceIcon?: string | null;    // appearance of the space, cards only
  spaceColor?: string | null;
  level: AccessLevel | null;    // effective (highest effective source); null = none applies now: a page with own access
                                //   and none of the sources, a token that can no longer authenticate
  sources: AccessSource[];      // strongest first
  note?: string | null;         // "4 hesla", "41 stránek přebírá přístup prostoru", "Vlastní přístup — nepřebírá běžná práva prostoru", "2 projekty"
}
// Which entries a card lists:
// - page: every page with a page grant for the principal (not only own-access pages); own-access pages have note
//   "Vlastní přístup — nepřebírá běžná práva prostoru". A WikiAdmin card has no page entries: its wiki entry covers them.
// - secret: only where secret grants exist. vault: note is the count of its passwords ("4 hesla").
// - youtrack: resourceId = the space id, title = the project ids joined, note like "2 projekty".

interface PrincipalAccess {
  principal: {
    kind: "user" | "group";
    id: string;
    name: string;
    detail?: string | null;   // username, or "12 členů"
    isActive?: boolean;
    isWikiAdmin?: boolean;
    lastSignInAt?: string | null;               // null on a space-scoped card (?spaceId=)
    groups?: { id: string; name: string }[] | null; // null on a space-scoped card (?spaceId=)
  };
  scope: "wiki" | "space" | "self";
  entries: AccessEntry[];
  canManage: boolean;        // caller may change grants shown here
}

// Who has access to one resource.
interface ResourceAccessEntry {
  subject: { kind: "user" | "group"; id: string; name: string; detail?: string | null; memberCount?: number | null; isActive?: boolean };
  level: AccessLevel | null;  // null: none of the subject's sources applies now (a grant not started or expired, an inactive
                              //   account); the UI says "Nemá" and the grant can still be changed or revoked
  sources: AccessSource[];
}
interface ResourceAccess {
  resource: { kind: string; id: string; title: string; spaceId?: string | null; spaceName?: string | null; parentTitle?: string | null; note: string | null };
  canManage: boolean;
  automatic: { label: string; detail: string; level: AccessLevel; count: number }[]; // "Správci wiki", "Správci prostoru" — locked rows
  entries: ResourceAccessEntry[];  // view=direct: only grants set here; view=effective: + people through groups
  hasMore: boolean;
  exceptions?: AccessException[];  // kind space only: what inside differs from the whole space
}
interface AccessException { kind: "page" | "vault" | "secret"; id: string; title: string; parentTitle?: string | null; mode: "own" | "grants"; grantCount: number }

interface AccessResource { kind: string; id: string; title: string; parentTitle?: string | null; spaceId?: string | null; spaceName?: string | null; note?: string | null }

// GET /api/access/candidates: the existing candidate shape.
interface AccessCandidate { id: string; kind: "user" | "group"; name: string; detail: string | null } // group: detail = its active member count as text
```

| Method and path | Who | What |
|---|---|---|
| `GET /api/access/kinds` | signed in | `AccessKindView[]` of the enabled kinds (YouTrack only when its module is configured). |
| `GET /api/access/me` | signed in | `PrincipalAccess` of the caller, scope `self`. |
| `GET /api/access/principals/user/{id}` | WikiAdmin: whole wiki. Space admin: `?spaceId=` required, only that space's entries. | `PrincipalAccess`. |
| `GET /api/access/principals/group/{id}` | same | `PrincipalAccess` of the group's own grants. |
| `GET /api/access/resources/{kind}/{id}?view=effective\|direct&q=` | who may manage the resource (space admin, vault Manage, page ManageAccess, WikiAdmin) | `ResourceAccess`; `effective` lists at most 200 entries, then `hasMore`. `q` (space) narrows the people and groups by name or username before that cap, so anyone can be found. |
| `GET /api/access/resources?kind=&q=` | caller | `AccessResource[]` (max 20) the caller may grant, for the "K čemu" step. |
| `GET /api/access/candidates?kind=user\|group&q=` | anyone who manages something | `AccessCandidate[]`: up to 20 active users or groups matching `q`; `q` needs one character at least (400 otherwise). No full directory, no inactive accounts. |
| `PUT /api/access/resources/{kind}/{id}/grants` | as the kind's writer | Body `{ subjectKind, subjectId, level, startAt, expiresAt, expectedVersion }`; 204. `403 reauth_required` as elsewhere (see the fresh sign-in below). |
| `DELETE /api/access/resources/{kind}/{id}/grants/{subjectKind}/{subjectId}?version=` | same | 204. |
| `GET /api/access/principals/user/{id}/revoke-all` | WikiAdmin | Impact preview: `{ grants, groups, tokens }` counts. |
| `POST /api/access/principals/user/{id}/revoke-all` | WikiAdmin + fresh sign-in | Closes all direct space, page, vault and secret grants and group memberships, and revokes personal API tokens. The account stays active; deactivating it remains a separate action. `409` with a message naming the spaces where the person holds the last permanent Admin; the UI shows the message. |
| `GET /api/admin/api-tokens` | WikiAdmin | All personal tokens: `{ id, userId, userName, name, scopes, createdAt, expiresAt, lastUsedAt, revokedAt }[]`. `lastUsedAt` is always `null`: token use is not recorded, so the UI never shows a last use. |
| `DELETE /api/admin/api-tokens/{id}` | WikiAdmin | Revokes someone's token (audited like the owner's revoke). |
| `GET /api/admin/access-audit` | WikiAdmin | Adds the filters `userId` (actor or subject), `groupId` and `spaceId` to today's `vaultId`. |

`GET /api/spaces/{id}/access` returns only `{ grants }`: the users and groups directory is gone. People and groups for a new grant come from `/api/access/candidates`. This closes the 2026-09-28 audit finding that a space admin gets every account, including inactive ones.

**Fresh sign-in.** Every guarded change of this layer (`space.grant` and `page.grant` with manage, every vault and secret grant, `account.wiki_admin` — the Správce wiki switch and creating a user as WikiAdmin — and `account.revoke_all`) binds to the global proof target: the challenge starts with `POST /api/security/reauth { operation: "security" }` without a vault or secret target, and the change carries the proof in `X-Atlas-Reauth`, or relies on the session's 10-minute window. The UI asks up front (ConfirmWithReauth) for the cases it knows and asks again when the server still answers `403 reauth_required`.

## 4. UI

Components live in `components/settings/access/`. They are generic: typed props in, events out, and no data access except in the page components that wire them.

- `AccessEntryRow` renders the sentence. The level badge (`AccessLevelBadge tone="level"`, always the full four-step stair) comes last, has a fixed width and sits at the right edge. The resource icon is tinted with the level colour. Actions (Změnit, Odebrat; the group for access through a group) fade in over the right end of the row on hover or focus. On touch devices they stay visible below the row.
- `LevelLegend` shows the colour key with counts; pressing a level filters by it.
- `PrincipalAccessView` is the card of a person or group, grouped by space ("Celý prostor" plus exceptions). It has one filter line: search, Druh, Odkud and the legend.
- `ResourceAccessPanel` shows who has access to one resource: locked automatic rows, then Skupiny, then Lidé. Efektivně / Jen přidělené, legend. It replaces `SpaceGrantEditor`, `VaultAccessEditor` and the list in `PageAccessPanel`.
- `PrincipalGrantFlow` adds access from a person: K čemu (the grantable kinds) → Který → Úroveň → Platnost → Souhrn. `GrantFlow` stays for the resource direction.
- `accessKinds.ts` is the kind registry. It is filled from `GET /api/access/kinds`; a plugin may add UI extras (tabs) from its own lazily loaded module.
- Level colours are the tokens `--level-{discover,read,write,manage}` and `-bg` in `settings.css`, in light and dark. `[data-level-color]` picks one.

Placement:

- Settings are three places, each with its own entry, rail heading, breadcrumb and way back: **Profil** (the avatar, `#/profile/<tab>`), **Nastavení prostoru** (the space's and wiki administrators, `#/spaces/<id>/settings/<section>`, with a switcher between the spaces the account manages) and **Správa wiki** (wiki administrators, the gear, `#/settings/<seg>/…`; formerly Nastavení wiki). Everyone who can open a space also has its tab **O prostoru** (`#/spaces/<id>/about`): the description, the account's own access and the templates, and for administrators links into the sections of Nastavení prostoru; it never lists people.
- **Správa wiki** gets these groups; the paths did not change, only the grouping:

  | Group | Items |
  |---|---|
  | (no label) | Přehled (`security/overview`), Audit (`security/audit`) |
  | **Lidé** | Lidé (`people/accounts`), Skupiny (`people/groups`), Přihlašování (`general/authentication`) |
  | **Prostory** | Prostory a přístupy (`access/spaces`), one item per plugin kind (`access/<kind>`) |
  | **Trezory a hesla** | Trezory (`security/vaults`), Žádosti o přístup (`security/requests`), Ochrana hesel (`security/protection`) |
  | **AI a integrace** | AI (`general/modules/ai`), one item per integration module (`general/modules/<id>`), API tokeny (`people/tokens`) |
  | **Provoz** | Zálohy (`general/backups`), Profily úložišť (`general/storage`), Umístění souborů (`general/files`), Sítě a proxy (`security/networks`), Import z Wiki.js (`general/modules/wiki-js-import`) |

- Přehled práv (`security/access`) is removed; old links go to Lidé.
- **Person card** `people/accounts/{id}[/tab]` has the tabs:
  - Přístupy (`PrincipalAccessView`);
  - Účet (today's AccountDetail: active, správce wiki, API, password);
  - Skupiny;
  - Tokeny;
  - Činnost (audit filtered by `userId`).

  The actions are Přidat přístup (`…/new`) and Odebrat vše….
- **Group card** `people/groups/{id}[/tab]` has the tabs Přístupy, Členové (today's GroupDetail) and Činnost.
- **Space in the centre** `access/spaces/{id}[/tab]` has the tabs:
  - Kdo má přístup;
  - Výjimky;
  - Odkazy (today's sharing page content);
  - Činnost.
- **Space settings › Přístupy** shows the same panel with the tabs Kdo má přístup and Výjimky. A person opens as a card limited to that space.
- **Vault detail › Přístupy** and the secret's inline access use `ResourceAccessPanel` (kind vault / secret).
- **Page › Kdo má přístup** keeps the mode cards (space defaults vs own access) and uses `ResourceAccessPanel` for the list.
- **Profile › Moje přístupy** (`#/profile/access`) shows `PrincipalAccessView` read-only from `/api/access/me`.

## 5. Adding a kind (plugins and future services)

1. Implement `IAccessKind` in the plugin's server module:
   - a descriptor with id, labels, icon, parent kind and the levels it uses, each with a sentence of what it allows;
   - `ForPrincipalAsync`;
   - `ForResourceAsync` if it lists who has access;
   - `SearchAsync` and an `IAccessGrantWriter` only if it has grants of its own.
2. Register it in the module's `AddXxx()` with `services.AddScoped<IAccessKind, …>()`.
3. Optionally, from the plugin's lazily loaded frontend module, call `registerAccessKind({ id, tabs })` for extra tabs on its resources.

With that, the kind appears in the group Prostory of Správa wiki, on every person's card, in the "K čemu" step when grantable, in "Kdo má přístup" and in the audit filter. `YouTrackAccessKind` (`Features/YouTrack`) is the reference: read-only, with access derived from the space role.
