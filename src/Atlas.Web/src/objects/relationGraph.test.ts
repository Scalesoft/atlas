// @vitest-environment node
import { describe, expect, it } from "vitest";
import { GROUP_LIMIT, groupId, openableId, prepare, relationItems, UNAVAILABLE_ID } from "./relationGraph";
import { plainTitle, type RelationEdge, type RelationGraph, type RelationNode } from "./relationsApi";

const node = (id: string, title: string | null = id, extra: Partial<RelationNode> = {}): RelationNode => ({ id, typeId: "atlas.page", title, spaceId: "s", spaceName: "Provoz", readable: true, ...extra });
const edge = (sourceId: string, targetId: string, kind: RelationEdge["kind"] = "link", count = 1): RelationEdge => ({ sourceId, targetId, kind, count });
const byId = (items: ReturnType<typeof relationItems>) => new Map(items.map(item => [item.id, item]));

describe("relationItems", () => {
  it("makes a card per node and a connector per edge, labelled and coloured by kind, the focus tinted", () => {
    const items = byId(relationItems({ nodes: [node("a", "**Úvod**"), node("b", "Tabule", { typeId: "atlas.board" })], edges: [edge("a", "b", "placement", 3)] }, { focusId: "a" }));
    expect(items.get("a")).toMatchObject({ kind: "page", color: "yellow", targetResourceId: "a", target: { typeId: "atlas.page", title: "Úvod", spaceTitle: "Stránka · Provoz" } });
    expect(items.get("b")).toMatchObject({ color: "teal", target: { spaceTitle: "Tabule · Provoz" } });
    expect(items.get("edge:placement:a>b")).toMatchObject({ kind: "connector", from: "a", to: "b", text: "je na tabuli ×3", color: "green" });
  });

  it("lays out sources before their targets", () => {
    const items = byId(relationItems({ nodes: [node("a"), node("b"), node("c")], edges: [edge("a", "b"), edge("b", "c")] }));
    expect(items.get("a")!.x).toBeLessThan(items.get("b")!.x);
    expect(items.get("b")!.x).toBeLessThan(items.get("c")!.x);
  });

  it("leaves out what the reader may not read, and a tree reference", () => {
    const items = byId(relationItems({ nodes: [node("a"), node("x", null, { readable: false }), node("c")], edges: [edge("a", "x"), edge("a", "c", "reference")] }));
    expect([...items.keys()].sort()).toEqual(["a", "c"]);
  });

  it("ends a link to a node that is gone or in the trash (no type) at the Nedostupné node", () => {
    const items = byId(relationItems({ nodes: [node("a"), node("t", null, { readable: false, typeId: null as unknown as string })], edges: [edge("a", "t")] }));
    expect([...items.keys()].sort()).toEqual(["a", `edge:link:a>${UNAVAILABLE_ID}`, UNAVAILABLE_ID].sort());
  });

  it("ends a link to something gone at one red, dashed Nedostupné node", () => {
    const items = byId(relationItems({ nodes: [node("a"), node("b")], edges: [edge("a", "gone1"), edge("b", "gone2")] }));
    expect(items.get(UNAVAILABLE_ID)).toMatchObject({ color: "red", target: { typeId: UNAVAILABLE_ID, title: "Nedostupné" } });
    expect(items.get(`edge:link:a>${UNAVAILABLE_ID}`)).toMatchObject({ color: "red", text: "rozbitý odkaz", props: { dash: "dashed", state: "blocked" } });
    expect(openableId(UNAVAILABLE_ID)).toBeNull();
  });

  it("is case-blind about ids and tolerates a cycle", () => {
    const { nodes, edges } = prepare({ nodes: [node("A"), node("B")], edges: [edge("a", "B"), edge("b", "A")] });
    expect(nodes.map(n => n.id)).toEqual(["a", "b"]);
    expect(edges).toHaveLength(2);
    expect(relationItems({ nodes: [node("A"), node("B")], edges: [edge("a", "B"), edge("b", "A")] })).toHaveLength(4);
  });
});

describe("plainTitle", () => {
  it("projects inline Markdown to its words", () => {
    expect(plainTitle("## **Úvod** do `Atlasu` a [odkazů](https://x.cz) \\*")).toBe("Úvod do Atlasu a odkazů *");
    expect(plainTitle("snake_case_name a *kurzíva*")).toBe("snake_case_name a kurzíva");
    expect(plainTitle(null)).toBe("");
  });
});

describe("folding into groups past the limit", () => {
  const many = (count: number): RelationGraph => ({
    nodes: [node("hub"), node("near"), ...Array.from({ length: count }, (_, index) => node(`n${index}`, `N${index}`, { spaceName: index % 2 ? "Lidé" : "Provoz" }))],
    edges: [edge("hub", "near"), ...Array.from({ length: count }, (_, index) => edge(index ? `n${index - 1}` : "near", `n${index}`))],
  });

  it("keeps the focus and its neighbours out of the groups and collapses the rest by branch", () => {
    const items = relationItems(many(GROUP_LIMIT + 10), { focusId: "hub", branchOf: n => (n.id === "n0" ? "Jedna" : undefined) });
    const ids = byId(items);
    expect(ids.get("hub")!.parent).toBeUndefined();
    expect(ids.get("near")!.parent).toBeUndefined();
    expect(ids.get(groupId("Provoz"))).toMatchObject({ kind: "group", props: { collapsed: true } });
    expect(ids.get(groupId("Lidé"))!.text).toBe(`Lidé · ${(GROUP_LIMIT + 10) / 2}`);
    // A branch of one stays a card; every member of a group points at it.
    expect(ids.get("n0")!.parent).toBeUndefined();
    expect(ids.get("n2")!.parent).toBe(groupId("Provoz"));
  });

  it("opens a group on request: the same items, the group open and its members inside its box", () => {
    const closed = byId(relationItems(many(GROUP_LIMIT + 10), { focusId: "hub" }));
    const open = byId(relationItems(many(GROUP_LIMIT + 10), { focusId: "hub", expanded: new Set([groupId("Provoz")]) }));
    expect(open.get(groupId("Provoz"))!.props?.collapsed).toBe(false);
    expect(open.size).toBe(closed.size);
    const box = open.get(groupId("Provoz"))!, member = open.get("n2")!;
    expect(member.x).toBeGreaterThanOrEqual(box.x);
    expect(member.y + member.height).toBeLessThanOrEqual(box.y + box.height);
  });

  it("does not fold a graph within the limit", () => {
    expect(relationItems(many(10), { focusId: "hub" }).some(item => item.kind === "group")).toBe(false);
  });
});
