-- The small picture of a contact (Atlas 3.5 package G): a company's logo or a person's photo the user uploads, at most 64 KiB, one per
-- contact. The contact's photo field names it (/api/contacts/{id}/photo?v=…); it goes with the contact's envelope. Existing contacts
-- have none and keep their photo field (an http(s) address, if any) as it is.
CREATE TABLE "ContactPhoto" (
  "ContactId" uuid PRIMARY KEY REFERENCES "ContentResource"("Id") ON DELETE CASCADE,
  "MimeType" text NOT NULL CONSTRAINT "CK_ContactPhoto_MimeType" CHECK ("MimeType" IN ('image/png', 'image/jpeg', 'image/webp')),
  "Sha256" text NOT NULL CONSTRAINT "CK_ContactPhoto_Sha256" CHECK ("Sha256" ~ '^[0-9a-f]{64}$'),
  "Content" bytea NOT NULL CONSTRAINT "CK_ContactPhoto_Content" CHECK (octet_length("Content") BETWEEN 1 AND 65536)
);
