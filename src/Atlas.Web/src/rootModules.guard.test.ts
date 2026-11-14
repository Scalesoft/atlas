import { readdirSync } from "node:fs";
import { describe, expect, it } from "vitest";

// The root of src holds the 91 modules below, left from before the code was sorted into feature folders (contacts/, boards/,
// search/, ...). The rule is to put a new module in the folder of its feature and to move a root module when it is touched, so
// this list only shrinks: a new root module fails the test, and so does a listed one that has moved (remove it from the list).
const ROOT_MODULES = [
  "App.vue", "accessApi.ts", "activity.ts", "administration.css", "administration.ts", "aiPreferences.ts", "api.ts",
  "apiTokenRights.ts", "appearance.ts", "channel.ts", "collaboration.ts", "collaborationPresence.css",
  "collaborationPresence.ts", "command-palette.css", "contentExport.ts", "contentImport.ts",
  "creationSuggestions.ts", "document.ts", "documentPresentation.ts",
  "documentWidgets.ts", "editorActions.ts", "editorDirectories.ts", "editorUploads.ts",
  "env.d.ts", "formatDate.ts", "history.ts", "historyBlocks.ts", "historyDiff.ts", "historyFormat.ts", "hub.ts",
  "iconPack.ts", "iconPackPart.ts", "idle.ts", "lazyComponent.ts", "main.ts", "markdownImport.ts", "mcpPublish.ts",
  "motion.css", "motionLiterals.baseline.ts", "notifications.ts", "openApiDetail.ts", "organization.ts",
  "page-icons.css", "page-tags.css", "pageConnections.ts", "pageDecorations.ts", "pageLinks.ts", "pageObjectDrag.ts",
  "pageSource.ts", "pageSourceDraft.ts", "pageTags.ts", "pageTemplateApi.ts", "pageTemplates.ts",
  "personalLibrary.ts", "profile.css", "profile.ts", "protection.ts", "reauthPopup.ts", "revealSettled.ts",
  "roles.ts", "savePage.ts", "searchArrival.ts", "searchGaps.ts", "searchGroupOrder.ts", "searchHighlight.ts",
  "searchMerge.ts", "searchPaging.ts", "searchRequest.ts", "searchSemantic.ts", "secretRelease.ts", "security.ts",
  "settingsPlaces.ts", "settingsRoutes.ts", "sharing.ts", "shell.css", "shellRoute.ts", "similarPages.ts",
  "spaceCover.ts", "spaceCoverCatalog.ts", "spaceCoverDrawings.ts", "spacePins.ts", "spaceSettings.ts",
  "spaceTwins.ts", "storageProfiles.ts", "test-setup.ts", "trashCleanupApi.ts", "treeExpansion.ts", "treeFilter.ts",
  "treeMove.ts", "vaults.ts",
];

describe("modules in the root of src", () => {
  it("are only the listed ones", () => {
    const found = readdirSync(import.meta.dirname, { withFileTypes: true })
      .filter(entry => entry.isFile() && /\.(ts|vue|css)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name))
      .map(entry => entry.name).sort();
    expect(found.filter(name => !ROOT_MODULES.includes(name)), "new modules go into the folder of their feature").toEqual([]);
    expect(ROOT_MODULES.filter(name => !found.includes(name)), "moved or deleted: remove from ROOT_MODULES").toEqual([]);
  });
});
