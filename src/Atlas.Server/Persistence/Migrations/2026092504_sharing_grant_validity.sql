-- Capabilities may never outlive the issuer's currently effective Admin authority.
-- VOLATILE observes the membership/grant mutation that fired the trigger.
CREATE FUNCTION atlas_share_creator_can_share_until(actor uuid, space uuid, required_expiry timestamptz)
RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT EXISTS (SELECT 1 FROM "UserAccount" u WHERE u."Id"=actor AND u."IsActive" AND
   (u."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId"=space AND g."Role"=3
     AND (g."ExpiresAt" IS NULL OR required_expiry <= g."ExpiresAt")
     AND (g."UserId"=actor OR EXISTS (SELECT 1 FROM "GroupMember" m
       WHERE m."UserId"=actor AND m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL)))))
$$;

CREATE OR REPLACE FUNCTION atlas_share_creator_is_admin(actor uuid, space uuid)
RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT atlas_share_creator_can_share_until(actor, space, statement_timestamp())
$$;

-- A remaining temporary Admin assignment must not preserve a longer-lived old token.
-- Closure is permanent: later grants/memberships do not reactivate these links.
CREATE OR REPLACE FUNCTION atlas_revoke_grant_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."Role"=3 AND OLD."ActiveTo" IS NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."SpaceId"=OLD."SpaceId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND (l."CreatedById"=OLD."UserId" OR EXISTS (SELECT 1 FROM "GroupMember" m
      WHERE m."GroupId"=OLD."GroupId" AND m."UserId"=l."CreatedById" AND m."ActiveTo" IS NULL))
    AND NOT atlas_share_creator_can_share_until(l."CreatedById",l."SpaceId",l."ExpiresAt");
 END IF;
 RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION atlas_revoke_membership_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."ActiveTo" IS NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."CreatedById"=OLD."UserId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND NOT atlas_share_creator_can_share_until(l."CreatedById",l."SpaceId",l."ExpiresAt");
 END IF;
 RETURN NULL;
END $$;
