import { globSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Plan §0a (phase 1, rewritten on purpose by phase 3 W8): one AI icon, drawn only by AiGlyph, and shown only next to model output
// or inputs that start a model task, plus explicit AI settings identity. Phase 1 had no model, so the rule was "nowhere in production". Phase 3 has the first model
// output, so the rule is now an explicit allowlist: the files below may import AiGlyph, and nothing else may. Each package that
// adds a model surface adds its own files here, one line each, and says why in its gallery example. Rule-based suggestions,
// search results, passages, related pages and sources carry the icon of their type instead.
const MODEL_SURFACES = [
  "components/ai/mountAiGlyph.ts", // same Vue glyph inside DOM-owned editor actions (P4-E)
  "components/ai/PageSummary.vue", // explicit page summary task (P4-R)
  "components/ai/ReadonlySelectionActions.vue", // explicit explain action (P4-R)
  "components/publishing/ChangeSummaryField.vue", // model change description proposal (P4-R)
  "components/publishing/PublishFindings.vue", // completed model spelling findings (P4-R)
  "components/creation/TagSuggestions.vue", // authorized existing tag proposal (P4-R)
  "components/search/SearchAnswerCard.vue", // explicit search answer request (W11)
  "components/search/SearchModeSwitch.vue", // explicit ask mode (W11)
  "components/ai/AiAnswer.vue", // the answer: model output, and the working state of a model task
  "components/ai/AiProvenance.vue", // the line of origin under model output
  "components/ai/AiStatus.vue", // the one status line of a running model task (refresh 2.5): search field, answer card, assistant
  "components/assistant/AssistantLauncher.vue", // entries that start a model task: sidebar item, „Zeptat se na tuto stránku“ (W10)
  "components/assistant/AssistantPanelHeader.vue", // the header of the Assistant panel (W10)
  "components/assistant/AssistantPanel.vue", // the empty assistant greets with the large glyph (refresh 2.5, size xl)
  "components/CommandIcon.vue", // AI palette commands and explicit AI settings identity
  "components/CollaboratorAvatars.vue", // AI identity: an agent writing into a page for a person, at work and done (3.6-H)
] as const;
// Surfaces that look close to a model one but are not: they must never import the glyph.
const NEVER_GLYPH = [
  "components/search/SemanticBadge.vue",
  "components/search/ResultPassage.vue",
  "components/PageConnections.vue",
  "components/SimilarPagesSuggestion.vue",
  "components/settings/SuggestionChip.vue",
  "components/ai/AiSourceList.vue",
  "components/ai/AiCitation.vue",
  "components/ai/AiFeedback.vue",
] as const;
// twinkle (in AiGlyph, when `working`) and shimmer (in the text of a working model task) are written only here.
// AiStatus is the status line every model task shows while it works (refresh 2.5; AiAnswer composes it), so its verb carries
// the shimmer.
const LOOP_FILES = ["components/ai/AiGlyph.vue", "components/ai/AiStatus.vue"] as const;
const sourceRoot = resolve(import.meta.dirname, "../..");
const production = globSync("**/*.{vue,ts}", { cwd: sourceRoot })
  .map((file) => file.split("\\").join("/"))
  .filter((file) => !file.startsWith("components/dev/") && !/\.test\.ts$/.test(file) && !file.endsWith(".d.ts"));
const importsGlyph = /(?:from\s*|import\s*\(\s*)["'][^"']*\/AiGlyph(?:\.vue)?["']/;
const read = (file: string) => readFileSync(resolve(sourceRoot, file), "utf8");
const using = (pattern: RegExp) => production.filter((file) => pattern.test(read(file)));

describe("AI glyph boundary", () => {
  it("draws sparkles only through AiGlyph", () => {
    expect(production.length).toBeGreaterThan(100);
    // appearanceIcons.ts keeps the retired page icon (and i18n/appearance-icons its name) so pages that already use it still render (plan O9);
    // appearanceCatalog.test.ts checks that no picker offers it.
    expect(using(/sparkles/)).toEqual(["i18n/appearance-icons.en.ts", "i18n/appearance-icons.ts", "components/settings/appearanceIcons.ts", "components/ai/AiGlyph.vue"]);
  });

  it("draws the AI glyph only on allowed model surfaces and AI identity", () => {
    expect(using(importsGlyph).sort()).toEqual([...MODEL_SURFACES].sort());
    for (const file of MODEL_SURFACES) expect(production, file).toContain(file);
    expect(using(/atlas-ai-(?:twinkle|shimmer)/).sort()).toEqual([...LOOP_FILES].sort());
    // The gallery is where the owner reviews it.
    const gallery = globSync("components/dev/**/*.vue", { cwd: sourceRoot }).filter((file) => importsGlyph.test(read(file)));
    expect(gallery.length).toBeGreaterThan(0);
  });

  it("keeps the glyph off search results, passages, related pages, suggestions, sources and citations", () => {
    for (const file of NEVER_GLYPH) {
      expect(production, file).toContain(file);
      // SuggestionChip only mentions the glyph in its docs: a model suggestion puts it into the `glyph` slot from outside.
      expect(read(file), file).not.toMatch(importsGlyph);
      expect(read(file), file).not.toMatch(/atlas-ai-(?:twinkle|shimmer)/);
    }
  });
});

// Atlas 2.0 MCP track: MCP is not an AI feature, so the token rights UI never draws the AI glyph (plan §0 rule 2).
describe("MCP token UI", () => {
  it("has no AI glyph, sparkles or model-work loops", () => {
    const files = ["apiTokenRights.ts", "components/auth/PersonalApiTokens.vue", "components/settings/people/AccountDetail.vue", "components/settings/people/ApiLevelPicker.vue", "components/settings/people/ApiTokenList.vue", "components/settings/people/ApiTokensPanel.vue"];
    for (const file of files) expect(read(file), file).not.toMatch(/AiGlyph|sparkles|atlas-ai-(?:twinkle|shimmer)/);
  });
});
