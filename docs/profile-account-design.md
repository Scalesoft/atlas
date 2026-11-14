# Profile account design

## Domain Model

An active UserAccount owns its local display name and optional contact email. These values are editable for builtin, external, and linked accounts. Email is unverified contact information: it is never a login identifier, uniqueness rule, account recovery target, or automatic external identity linking key. Provider identity continues to be issuer plus subject. Updating the local name never changes the provider.

A successful application sign-in is an immutable operational fact. The current session's authentication time and method live in the cookie; the most recent persisted fact may belong to another device. Password changes refresh the cookie stamp while preserving its original authentication claims and expiry. Unknown legacy authentication times remain null.

## Persistence Model

Migration 025 adds nullable Email varchar(254) to UserAccount and UserAccountRevision. Blank input becomes null; nonblank input must be a single email address with no control or whitespace characters, at most 254 characters. Email deliberately has no unique index because shared contact mailboxes do not establish account identity. The existing account anchor/current projection and trigger-managed revision history remain authoritative. Existing rows retain null email, with no invented backfill.

UserAccountRevision retains its own UUID Id, durable UserId FK, ChangedBy actor FK, ActiveFrom/ActiveTo timestamptz half-open UTC interval, current partial unique index, overlap exclusion, and positive interval check. The existing trigger closes the current row and inserts a successor atomically. Its explicit column list includes Email. Profile writes lock the account anchor, validate the current stamp/active state, compare the existing revision ActiveFrom opaque version, and update both fields in one transaction. Unchanged values create no revision. Administration changes and profile changes therefore share CAS.

UserSignIn is a bounded operational fact table: Id UUID primary key; UserId UUID NOT NULL FK to UserAccount(Id), default restrictive deletion; AuthenticatedAt timestamptz NOT NULL with finite-time check; AuthenticationMethod varchar(100) NOT NULL with nonblank check. IX_UserSignIn_UserTime indexes (UserId, AuthenticatedAt DESC, Id DESC). No IP, user-agent, tokens, passwords, or duplicated provider names are stored. A sign-in transaction locks and revalidates the user anchor before insertion and retains the newest 100 facts per user. Retention deletes old operational facts; it does not alter temporal business history. This bounded history makes last-sign-in evidence available without an unbounded audit store. No historical sign-ins are backfilled.

Migration follows the incumbent PascalCase SQL identifiers and UUID key dialect. UTC DateTime/NHibernate UtcDateTime follows existing application mappings. It is expand-only and, like existing migrations, requires verified-backup restore for downgrade.

## HTTP contract and test seams

Cookie-only GET and PUT /api/profile/account use the existing current-user validation; PUT also passes the host CSRF middleware. Neither accepts a target UserId. GET and successful PUT return displayName, email, version, source (builtin/external/linked), canEditDisplayName, canEditEmail, currentSessionAuthenticatedAt, currentSessionAuthenticationMethod, lastSignInAt, and lastSignInMethod. PUT accepts displayName, email, expectedVersion. Legacy unavailable timestamps and methods are null. Existing security and identities endpoints supply their own settings.

Tests observe real HTTP cookie/CSRF boundaries and PostgreSQL history/CAS behavior: self-service by a non-admin, anonymous/bearer/missing-CSRF denial, stale/concurrent versions, unchanged writes, validation, immediate revocation, retained account audit, bounded sign-in facts, and password refresh preserving current-session authentication while other-device sign-in changes the latest fact. Unit-level session refresh uses the existing authentication-service seam. Tests never justify schema changes.
