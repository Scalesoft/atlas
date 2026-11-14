-- WP4 V6: secret ids admitted into the live collaborative state of a page generation. The Yjs state is opaque to the server,
-- so the collaboration guard records every reference it admits; joining the session requires Discover on each of them (and on
-- those of the base revision). Rows follow the generation of an additive schema upgrade (ON UPDATE CASCADE) and are deleted
-- when the live state is reset.
CREATE TABLE "CollaborationSecretReference" (
    "PageId" uuid NOT NULL,
    "Generation" uuid NOT NULL,
    "SecretId" uuid NOT NULL,
    PRIMARY KEY ("PageId", "Generation", "SecretId"),
    FOREIGN KEY ("PageId", "Generation") REFERENCES "PageCollaboration"("PageId", "Generation") ON UPDATE CASCADE ON DELETE CASCADE
);
