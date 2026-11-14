-- A run of the contacts' link migration also ends as failed (a round failed repeatedly, or the admin who started it is gone) or
-- cancelled (by a WikiAdmin), so a broken run no longer holds UX_ContactLinkRun_Running and blocks every next one.
ALTER TABLE "ContactLinkRun" DROP CONSTRAINT "ContactLinkRun_State_check";
ALTER TABLE "ContactLinkRun" ADD CONSTRAINT "CK_ContactLinkRun_State" CHECK ("State" IN ('running', 'done', 'failed', 'cancelled'));
