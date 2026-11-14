import { expect, it } from "vitest";
import { createPageTemplateContent } from "./pageTemplates";

// U2-4: a deployment page from the catalog starts with its summary and says how the requirements come in.
it("starts a deployment from the catalog with the summary block and the way to insert its requirements", async () => {
  const content = await createPageTemplateContent("projector-deployment");
  const value = JSON.parse(content.contentJson);
  expect(value.plugins).toEqual(expect.arrayContaining([{ id: "atlas-requirements", version: 2 }]));
  expect(value.document.content[0]).toMatchObject({ type: "overview", attrs: { id: expect.stringMatching(/^[0-9a-f-]{36}$/), kind: "requirements" } });
  // U3-3: the way in is the editor's placeholder on the empty line, never text of the page that gets published and copied.
  expect(value.document.content.slice(1)).toEqual([{ type: "paragraph" }]);
});

it("hints at the catalog on the empty line after a lone summary while editing, as a placeholder", async () => {
  const [{ readFileSync }, { resolve }] = await Promise.all([import("node:fs"), import("node:path")]);
  const css = readFileSync(resolve(__dirname, "shell.css"), "utf8");
  expect(css).toMatch(/> \[data-overview-kind="requirements"\]:first-child \+ p:last-child:has\(> br\.ProseMirror-trailingBreak:only-child\)::before \{[^}]*Moduly produktu/);
});

// P15 „Struktura z šablon“: the project's people are places (unresolved contact chips named by their role), never free text.
it("starts a project with places for PM and „čekáme na“, and with Projector an empty requirement heading under „Požadavky“", async () => {
  type Node = { type: string; attrs?: { data?: { contacts: object[] }; id?: string }; content?: Node[]; text?: string };
  const flat = (nodes: Node[] = []): Node[] => nodes.flatMap(node => [node, ...flat(node.content)]);
  const plain = JSON.parse((await createPageTemplateContent("project")).contentJson);
  expect(plain.plugins).toEqual(expect.arrayContaining([{ id: "contacts", version: expect.any(Number) }]));
  const places = flat(plain.document.content).filter(node => node.type === "contactInline").map(node => node.attrs!.data!.contacts[0]);
  expect(places).toEqual([{ name: "PM", role: "PM", unresolved: true }, { name: "Čekáme na", role: "Čekáme na", unresolved: true }]);
  expect(flat(plain.document.content).some(node => node.type === "requirement")).toBe(false);

  const withProjector = JSON.parse((await createPageTemplateContent("project", { projector: true })).contentJson);
  expect(withProjector.plugins).toEqual(expect.arrayContaining([{ id: "atlas-requirements", version: 4 }]));
  const blocks: (Node & { attrs?: { level?: number; state?: string } })[] = withProjector.document.content;
  const heading = blocks.findIndex(node => node.type === "heading" && node.content?.[0]?.text === "Požadavky");
  // A requirement heading one level under „Požadavky“ (never the legacy block, which the server's conversion would refuse without a title).
  expect(blocks[heading + 1]).toEqual({ type: "heading", attrs: { level: blocks[heading]!.attrs!.level! + 1, state: "planned" } });
  expect(flat(withProjector.document.content).some(node => node.type === "requirement")).toBe(false);
}, 30_000);
