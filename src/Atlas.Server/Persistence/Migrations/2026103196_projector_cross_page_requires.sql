-- „Co to blokuje“ (CrossPageRequirements.DependentsAsync) looks for the blocks that require key@pageId of another page: only the rows
-- with a cross-page reference (an '@' in their raw list) are candidates, read by this partial index instead of the whole index table.
CREATE INDEX "IX_ProjectorRequirementIndex_CrossPage" ON "ProjectorRequirementIndex"("PageId") WHERE "Requires" LIKE '%@%';
