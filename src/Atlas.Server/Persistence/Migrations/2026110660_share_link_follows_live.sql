-- 3.6-I: an anonymous share link follows a live page's working state only when the page was Živě as the link was created. Every
-- existing link was created before Živě existed: it keeps showing the version it pinned, whatever mode the page or space gets later.
ALTER TABLE "PageShareLink" ADD COLUMN "FollowsLive" boolean NOT NULL DEFAULT false;
