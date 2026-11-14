-- Atlas 3.6: „Řadit abecedně“ pins one level of the space tree to its titles (TitleOrder). A page holds the choice for its direct
-- children, the space's tree row for the root. The tree read orders a pinned level; nothing in the database sorts. Every existing
-- level keeps its order: no level is pinned.
ALTER TABLE "Page" ADD COLUMN "SortChildrenByTitle" boolean NOT NULL DEFAULT false;
ALTER TABLE "SpaceOrganization" ADD COLUMN "SortRootByTitle" boolean NOT NULL DEFAULT false;
