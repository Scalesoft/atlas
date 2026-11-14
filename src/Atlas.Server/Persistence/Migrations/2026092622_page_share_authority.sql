-- WP3: a share link lives while its issuer holds Share on the page for the whole life of the link, exactly as
-- ExternalSharingAccess.CanShareUntilAsync decides on every open: WikiAdmin, a space Admin grant, or a page DocumentManager
-- grant (ResourceGrant role 3, the page legs of EffectivePageAccess role 3), unbounded or expiring no earlier than the link.
-- Before this migration the invalidation triggers counted only the space role: losing a space Admin grant closed the links of
-- an issuer who still managed the page, and closing a page grant closed nothing (the open-time check refused the link).
-- VOLATILE deliberately sees the grant or membership change made by the firing statement. New migration only; historical
-- function definitions remain immutable, and the space-only atlas_share_creator_can_share_until stays as it is.

CREATE FUNCTION atlas_share_creator_can_share_page_until(actor uuid, space uuid, page uuid, required_expiry timestamptz)
RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT atlas_share_creator_can_share_until(actor, space, required_expiry)
   OR EXISTS (SELECT 1 FROM "UserAccount" u
     JOIN "EffectiveResourceGrant" g ON g."PageId"=page AND g."Role"=3
     WHERE u."Id"=actor AND u."IsActive"
       AND (g."ExpiresAt" IS NULL OR required_expiry <= g."ExpiresAt")
       AND (g."UserId"=actor OR EXISTS (SELECT 1 FROM "GroupMember" m
         WHERE m."UserId"=actor AND m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL)))
$$;

CREATE OR REPLACE FUNCTION atlas_revoke_grant_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."Role"=3 AND OLD."ActiveTo" IS NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."SpaceId"=OLD."SpaceId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND (l."CreatedById"=OLD."UserId" OR EXISTS (SELECT 1 FROM "GroupMember" m
      WHERE m."GroupId"=OLD."GroupId" AND m."UserId"=l."CreatedById" AND m."ActiveTo" IS NULL))
    AND NOT atlas_share_creator_can_share_page_until(l."CreatedById",l."SpaceId",l."PageId",l."ExpiresAt");
 END IF;
 RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION atlas_revoke_membership_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."ActiveTo" IS NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."CreatedById"=OLD."UserId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND NOT atlas_share_creator_can_share_page_until(l."CreatedById",l."SpaceId",l."PageId",l."ExpiresAt");
 END IF;
 RETURN NULL;
END $$;

-- Closing a page DocumentManager grant (ResourceGrant rows are only ever closed, see TR_ResourceGrant_Immutable) closes the
-- links of that page whose issuer is left without Share authority covering them. Never reopens a link.
CREATE FUNCTION atlas_revoke_resource_grant_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."Role"=3 AND OLD."ActiveTo" IS NULL AND NEW."ActiveTo" IS NOT NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."PageId"=OLD."PageId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND (l."CreatedById"=OLD."UserId" OR EXISTS (SELECT 1 FROM "GroupMember" m
      WHERE m."GroupId"=OLD."GroupId" AND m."UserId"=l."CreatedById" AND m."ActiveTo" IS NULL))
    AND NOT atlas_share_creator_can_share_page_until(l."CreatedById",l."SpaceId",l."PageId",l."ExpiresAt");
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER "TR_ResourceGrant_RevokeShares" AFTER UPDATE ON "ResourceGrant" FOR EACH ROW EXECUTE FUNCTION atlas_revoke_resource_grant_shares();
