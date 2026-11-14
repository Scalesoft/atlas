-- Operational collaboration state: immutable business revisions remain in PageRevision.
CREATE TABLE "PageCollaboration" (
    "PageId" uuid PRIMARY KEY REFERENCES "Page"("Id"),
    "Generation" uuid NOT NULL,
    "BaseRevisionId" uuid NOT NULL,
    "Sequence" bigint NOT NULL DEFAULT 0 CHECK ("Sequence" >= 0),
    "SnapshotSequence" bigint NOT NULL DEFAULT 0,
    "SchemaBound" boolean NOT NULL DEFAULT false,
    "SchemaJson" text NULL,
    "State" bytea NULL,
    "StateSha256" varchar(64) NULL,
    "ProjectionJson" text NULL,
    "ProjectionMarkdown" text NULL,
    "ProjectionText" text NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    UNIQUE ("PageId", "Generation"),
    FOREIGN KEY ("PageId", "BaseRevisionId") REFERENCES "PageRevision"("PageId", "Id"),
    CHECK ("SnapshotSequence" BETWEEN 0 AND "Sequence"),
    CHECK ("State" IS NULL OR octet_length("State") <= 4194304),
    CHECK ("StateSha256" IS NULL OR "StateSha256" ~ '^[a-f0-9]{64}$'),
    CHECK (("State" IS NULL AND "StateSha256" IS NULL AND "ProjectionJson" IS NULL AND "ProjectionMarkdown" IS NULL AND "ProjectionText" IS NULL)
        OR ("State" IS NOT NULL AND "StateSha256" IS NOT NULL AND "ProjectionJson" IS NOT NULL AND "ProjectionMarkdown" IS NOT NULL AND "ProjectionText" IS NOT NULL))
);

-- Covered records double as bounded, byte-preserving idempotency receipts.
-- Uncovered records are the durable recovery tail and are never pruned.
CREATE TABLE "CollaborationUpdate" (
    "PageId" uuid NOT NULL,
    "Generation" uuid NOT NULL,
    "Sequence" bigint NOT NULL CHECK ("Sequence" > 0),
    "ClientUpdateId" uuid NOT NULL,
    "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ReceivedAt" timestamptz NOT NULL,
    "Payload" bytea NOT NULL CHECK (octet_length("Payload") BETWEEN 1 AND 4194304),
    PRIMARY KEY ("PageId", "Generation", "Sequence"),
    UNIQUE ("PageId", "Generation", "ClientUpdateId"),
    FOREIGN KEY ("PageId", "Generation") REFERENCES "PageCollaboration"("PageId", "Generation")
);
