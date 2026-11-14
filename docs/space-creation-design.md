# Optional access during space creation

MVP §2 allows a WikiAdmin to choose local groups and their roles while creating a space. The existing `Space`, `SpaceGrant` and group identities remain the domain model: one space has grants to users or groups, and the highest applicable role wins. The creator always receives a direct Admin grant. No selected groups means no additional access; WikiAdmin retains global access.

`CreateSpace(Name, Icon, Color)` keeps its existing constructor and adds optional `Groups`, containing `groupId` and `role` (`Reader`, `Writer`, `Admin`). Missing/null/empty groups preserve existing behavior. At most 100 unique groups may be assigned in one request. Invalid roles, duplicate identities, and missing groups reject the whole request.

Persistence: no schema, mapping or lifecycle changes. Creation reuses the existing actor/membership lock, current security-stamp and WikiAdmin checks, audit actor, temporal grant triggers and one NHibernate transaction. Group rows are validated together and locked against deletion until commit. Creation has no prior revision for CAS; subsequent access changes retain the existing CAS API.

The sidebar form loads the existing WikiAdmin-only group directory when opened, offers optional group/role rows, prevents repeated submission, and keeps input after failure. Cancel, Escape and outside click close an idle form without creating anything. During a request dismissal is disabled. Success adds/selects the returned space through the existing App flow.

Verification steps: first failing real HTTP/PostgreSQL tests for selected grants, private defaults, invalid-input rollback, CSRF and non-WikiAdmin rejection; first failing component tests for optional selection, retained values, busy dismissal and successful submission; implement against those public seams; run targeted tests/typecheck and independent review. No external tenant or deployment changes are needed.
