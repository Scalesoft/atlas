-- The merged contacts (contacts spec §5, mergedInto in the data of a trashed contact) are read by every mention relation to follow
-- them (ContactMentionRelations): a partial index keeps that read to the few merged ones instead of every contact.
CREATE INDEX "IX_ContentResource_MergedInto" ON "ContentResource" (("Data"->>'mergedInto'))
  WHERE "TypeId" = 'atlas.contact' AND "Data"->>'mergedInto' IS NOT NULL;
