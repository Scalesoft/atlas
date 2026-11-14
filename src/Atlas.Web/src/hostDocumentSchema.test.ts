import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { describeMarkdownSchema } from "@atlas/markdown/schema";
import { atlasContentPlugins } from "./documentWidgets";

// The server validates every stored document against this manifest (Platform/Validation/HostDocumentValidator.cs embeds it), so it
// describes the content plugins of the page editor, which DocumentEditor takes from the same factory. Regenerate after a plugin or
// editor change: ATLAS_UPDATE_SCHEMA=1 pnpm exec vitest run src/hostDocumentSchema.test.ts
const path = resolve(dirname(fileURLToPath(import.meta.url)), "../../Atlas.Server/Platform/Validation/host-markdown-schema.json");

describe("host document schema", () => {
  const described = describeMarkdownSchema(atlasContentPlugins({ kind: "edit" }), { profile: "rich" });
  const manifest = `${JSON.stringify(described, null, 2)}\n`;
  if (process.env.ATLAS_UPDATE_SCHEMA === "1") writeFileSync(path, manifest);

  it("is the manifest the server embeds", () => {
    expect(readFileSync(path, "utf8").replace(/\r\n/g, "\n")).toBe(manifest);
  });
  it("holds Projector's requirement blocks with their flat attributes", () => {
    const nodes = (JSON.parse(manifest) as { nodes: { name: string; owner?: string; content: string; attrs: { name: string }[] }[] }).nodes;
    expect(nodes.find(node => node.name === "requirement")).toMatchObject({ owner: "atlas-requirements", content: "paragraph block*" });
    expect(nodes.find(node => node.name === "requirement")!.attrs.map(attr => attr.name))
      .toEqual(["id", "key", "direction", "state", "stateLabel", "date", "party", "partyContactId", "where", "system", "requires"]);
    expect(nodes.find(node => node.name === "overview")).toMatchObject({ owner: "atlas-requirements", content: "" });
    expect(nodes.find(node => node.name === "overview")!.attrs.map(attr => attr.name)).toEqual(["id", "kind"]);
    // Atlas 3.3's summary stays readable in a live shared document until the content repair renames it (legacyDocument.ts).
    expect(nodes.find(node => node.name === "requirementSummary")).toMatchObject({ owner: "atlas-requirements", content: "" });
  });
  it("holds a requirement as a heading: its attributes and the dependency line (atlas-requirements 4, heading-anchors 2)", () => {
    const nodes = (JSON.parse(manifest) as { nodes: { name: string; attrs: { name: string; owner?: string }[] }[] }).nodes;
    const owned = (name: string) => nodes.find(node => node.name === name)!.attrs.filter(attr => attr.owner).map(attr => `${attr.owner}:${attr.name}`);
    expect(owned("heading")).toEqual(["heading-anchors:headingId", "heading-anchors:aliases", "atlas-requirements:state", "atlas-requirements:stateLabel",
      "atlas-requirements:direction", "atlas-requirements:system", "atlas-requirements:party", "atlas-requirements:partyContactId", "atlas-requirements:from"]);
    expect(owned("paragraph")).toEqual(["atlas-requirements:dependencies"]);
  });
  // The server takes any value of an attribute without a rule: a plugin of Atlas (the requirement, the next module's) declares one as the
  // editor's own plugins must (src/Atlas.Markdown/src/schema/manifest.test.ts).
  it("declares a constraint for every attribute and describes every structured validator", () => {
    const attributes = [...described.nodes.map(node => ["nodes", node] as const), ...described.marks.map(mark => ["marks", mark] as const)]
      .flatMap(([kind, type]) => type.attrs.map(attribute => ({ key: `${kind}:${type.name}:${attribute.name}`, attribute })));
    expect(attributes.filter(({ attribute }) => attribute.constraint.kind === "unconstrained").map(({ key }) => key),
      "Declare the rule next to the validator (defineMarkdownAttributeValidator)").toEqual([]);
    const named = new Set(attributes.flatMap(({ attribute }) => attribute.constraint.kind === "structured" ? [attribute.constraint.validator] : []));
    expect([...named].filter(name => !described.validators[name])).toEqual([]);
  });
  it("names the kind of entity of every inline atom", () => {
    expect(described.nodes.filter(node => node.owner && node.inline && !node.content).map(node => node.name)
      .filter(name => !described.entities.some(entity => entity.inline === name)),
    "Declare the kind with defineMarkdownEntityKind in the plugin that owns the node").toEqual([]);
  });
});
