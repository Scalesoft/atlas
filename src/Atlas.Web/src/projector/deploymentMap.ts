import type { BoardChange, BoardProps } from "../boards/boardApi";
import { jsonText, type OverviewJson } from "../overview/overviewKinds";
import { factsOfText, linkTarget } from "./requirementHeading";
import { requiredKeys, slugKey } from "./requirementModel";
import { requirementDefaults, type RequirementAttributes, type RequirementState } from "./requirementTypes";

// The deployment of one page as its board shows it (plan 3.4 §5): products (the outermost headings over blocks) › categories ›
// modules, and the external services of the inputs (`system`). Read from the stored page (its JSON), never stored itself; the
// board's product and service cards and „Poskládat tabuli“ read the same. Headings follow RequirementText.Sectioned on the server:
// a heading closes the ones of its level and deeper, a heading inside a block starts nothing, a nested block is in its parent's.

/** A stored page document as the map reads it. */
export type DeploymentDocument = OverviewJson;

export interface DeploymentBlock { readonly id: string; readonly title: string; readonly attrs: RequirementAttributes }
/** A heading-led part of a product: a module's blocks, everything under its heading. */
export interface DeploymentModule { readonly name: string; readonly anchor: string; readonly blocks: DeploymentBlock[] }
export interface DeploymentCategory { readonly name: string; readonly modules: DeploymentModule[] }
export interface DeploymentProduct {
  readonly name: string;
  /** The product heading's id (its headingId, else the slug of its text): the card's anchor. */
  readonly anchor: string;
  /** Blocks right under the product heading: the infrastructure (server, RDP, DNS, SSL…), the card's tags. */
  readonly base: DeploymentBlock[];
  readonly categories: DeploymentCategory[];
  readonly blocks: DeploymentBlock[];
}
export interface DeploymentService {
  readonly name: string;
  readonly anchor: string;
  /** Each input of the service once, whichever product holds or needs it. */
  readonly inputs: DeploymentBlock[];
  /** The products that hold or need its inputs, by name. */
  readonly products: string[];
}
/**
 * A product's use of a service: the modules that use it and how many of its inputs that takes. `internal`: the `system` names another
 * product of the page (the portal's link to MPI), so `service` is that product's anchor and the connector runs between the two cards.
 */
export interface DeploymentLink { readonly product: string; readonly service: string; readonly modules: string[]; readonly count: number; readonly state: RequirementState; readonly internal?: true }
export interface DeploymentMap { readonly products: DeploymentProduct[]; readonly services: DeploymentService[]; readonly links: DeploymentLink[] }

/** A module without a category heading over it (RequirementTree.Core on the server). */
export const coreCategory = "Jádro";

const validAnchor = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
/** A projected heading id, valid for one page version only (HeadingIds.IsTemporary): a board refuses it as an anchor. */
const temporaryAnchor = /^v\d+-h\d+$/;
/** A heading's anchor: its stable id, else the slug of its text (as the server names a module, RequirementTree.Definitions). */
function headingAnchor(node: OverviewJson, text: string) {
  const id = node.attrs?.headingId;
  return typeof id === "string" && validAnchor.test(id) && !temporaryAnchor.test(id) ? id : slugKey(text).replace(/\./g, "-");
}
/** A service's anchor on a board: its name's slug, so „NIA“ and „nia“ are one service and the anchor passes the board's rule. */
export const serviceAnchor = (name: string) => slugKey(name).replace(/\./g, "-");

function blockOf(node: OverviewJson): DeploymentBlock {
  const title = node.content?.[0] ? jsonText(node.content[0]).trim() : "";
  return { id: String(node.attrs?.id ?? ""), title, attrs: { ...requirementDefaults, ...node.attrs } as RequirementAttributes };
}

/** Blocked over waiting over all done over moving: the state a module, product or service shows (Projector's diagram). */
export function rollup(blocks: readonly DeploymentBlock[]): RequirementState {
  const states = blocks.map(block => block.attrs.state).filter(state => state !== "not_applicable");
  if (states.includes("blocked")) return "blocked";
  if (states.includes("waiting_for_input")) return "waiting_for_input";
  if (states.length && states.every(state => state === "done")) return "done";
  return states.some(state => state === "in_progress" || state === "done") ? "in_progress" : "planned";
}
/** x/y: done of those that apply. */
export const progress = (blocks: readonly DeploymentBlock[]) =>
  ({ done: blocks.filter(block => block.attrs.state === "done").length, total: blocks.filter(block => block.attrs.state !== "not_applicable").length });

const isHeading = (node: OverviewJson | undefined) => node?.type === "heading";
const isRequirementHeading = (node: OverviewJson | undefined) => isHeading(node) && typeof node!.attrs?.state === "string";
const headingIdOf = (node: OverviewJson) => typeof node.attrs?.headingId === "string" ? node.attrs.headingId : "";
const contactOf = (node: OverviewJson) => (node.attrs?.data as { contacts?: { name?: unknown }[] } | undefined)?.contacts?.[0];
/** A paragraph's text as the facts rule reads it (RequirementHeadings.Text): a contact as its name, an issue as its key, a link as its title. */
const factsText = (paragraph: OverviewJson) => (paragraph.content ?? []).map(child => child.type === "text" ? child.text ?? "" : child.type === "hardBreak" ? " "
  : child.type === "contactInline" ? String(contactOf(child)?.name ?? "") : child.type === "youtrackIssue" ? String(child.attrs?.key ?? "").replace(/^ +| +$/g, "")
  : child.type === "pageReference" ? String(child.attrs?.title ?? "") : "").join("");
const text = (value: unknown) => typeof value === "string" ? value : "";

/**
 * The document's top-level nodes with each requirement heading's section (up to the next heading of any level) in its heading's place as
 * the old block (RequirementHeadings.Project and View on the server): id and key the heading's id, its state, kind, service and
 * provenance, the facts line's date and the heading's party, the dependency line's requires (this page's by id, another's as `pageId#headingId`), the
 * heading's text as the title and the rest of the section as notes. A requirement heading is a block, never a module.
 */
export function projectRequirementHeadings(content: readonly OverviewJson[]): readonly OverviewJson[] {
  if (!content.some(isRequirementHeading)) return content;
  const local = new Set(content.filter(isRequirementHeading).map(headingIdOf).filter(Boolean)), nodes: OverviewJson[] = [];
  for (let at = 0; at < content.length; at++) {
    const heading = content[at]!;
    if (!isRequirementHeading(heading)) { nodes.push(heading); continue; }
    let end = at + 1;
    while (end < content.length && !isHeading(content[end])) end++;
    const deps = at + 1 < end && content[at + 1]!.type === "paragraph" && content[at + 1]!.attrs?.dependencies === true ? at + 1 : -1, next = (deps < 0 ? at : deps) + 1;
    const facts = next < end && content[next]!.type === "paragraph" ? factsOfText(factsText(content[next]!)) : null, factsAt = facts ? next : -1;
    const requires: string[] = [];
    for (const child of deps < 0 ? [] : content[deps]!.content ?? []) {
      const target = child.type === "pageReference" ? linkTarget(child.attrs?.id, child.attrs?.url) : null;
      const key = target && (local.has(target.headingId) ? target.headingId : `${target.pageId}#${target.headingId}`);
      if (key && !requires.includes(key)) requires.push(key);
    }
    const id = headingIdOf(heading), attrs = heading.attrs!;
    nodes.push({ type: "requirement", attrs: { id, key: id, direction: text(attrs.direction), state: text(attrs.state), stateLabel: text(attrs.stateLabel), system: text(attrs.system),
      from: text(attrs.from), date: facts?.deadline ?? "", party: text(attrs.party), partyContactId: text(attrs.partyContactId), where: "", requires: requires.join(","), level: Number(attrs.level ?? 1) },
      content: [{ type: "paragraph", ...(heading.content?.length ? { content: heading.content } : {}) }, ...content.slice(at + 1, end).filter((_, index) => at + 1 + index !== deps && at + 1 + index !== factsAt)] });
    at = end - 1;
  }
  return nodes;
}

export function deploymentMap(document: OverviewJson): DeploymentMap {
  const products: DeploymentProduct[] = [];
  const open: { text: string; level: number; anchor: string }[] = [];
  const add = (node: OverviewJson) => {
    const top = open[0];
    // Blocks before the first heading, or under a heading with no text to anchor a card to, belong to no product.
    if (!top?.anchor) return;
    let product = products.find(entry => entry.anchor === top.anchor);
    if (!product) products.push(product = { name: top.text, anchor: top.anchor, base: [], categories: [], blocks: [] });
    const blocks: DeploymentBlock[] = [];
    const walk = (block: OverviewJson) => { blocks.push(blockOf(block)); block.content?.forEach(child => child.type === "requirement" && walk(child)); };
    walk(node);
    product.blocks.push(...blocks);
    // The nearest heading is the module, the one above it its category (Jádro when that is the product itself).
    if (open.length === 1) { product.base.push(...blocks); return; }
    const module = open.at(-1)!, categoryName = open.length > 2 ? open.at(-2)!.text : coreCategory;
    let category = product.categories.find(entry => entry.name === categoryName);
    if (!category) product.categories.push(category = { name: categoryName, modules: [] });
    let found = category.modules.find(entry => entry.anchor === module.anchor);
    if (!found) category.modules.push(found = { name: module.text, anchor: module.anchor, blocks: [] });
    found.blocks.push(...blocks);
  };
  const walk = (nodes: readonly OverviewJson[]) => nodes.forEach(child => {
    if (child.type === "heading") {
      const level = Number(child.attrs?.level ?? 1), text = jsonText(child).trim();
      while (open.length && open.at(-1)!.level >= level) open.pop();
      open.push({ text, level, anchor: headingAnchor(child, text) });
    } else if (child.type === "requirement") add(child);
    else walk(child.content ?? []);
  });
  // A requirement heading is a block (its view), never a module: a module is a plain heading that holds requirements (DeploymentBoard.Of).
  walk(projectRequirementHeadings(document.content ?? []));

  const services = new Map<string, { name: string; anchor: string; inputs: DeploymentBlock[]; products: Set<string> }>();
  for (const product of products) for (const block of product.blocks) {
    const name = block.attrs.system.trim(), anchor = name && serviceAnchor(name);
    if (!anchor) continue;
    let service = services.get(anchor);
    if (!service) services.set(anchor, service = { name, anchor, inputs: [], products: new Set() });
    service.inputs.push(block);
  }
  const byKey = new Map(products.flatMap(product => product.blocks).filter(block => block.attrs.key).map(block => [block.attrs.key, block]));
  const links: DeploymentLink[] = [];
  // A `system` that names a product of the page is that product, no external service.
  const productOf = new Map(products.map(product => [serviceAnchor(product.name), product.anchor]));
  for (const product of products) {
    const moduleOf = new Map(product.categories.flatMap(category => category.modules).flatMap(module => module.blocks.map(block => [block, module.name] as const)));
    for (const service of services.values()) {
      const used = new Set<DeploymentBlock>(), modules = new Set<string>();
      for (const block of product.blocks) {
        const own = service.inputs.includes(block), needed = requiredKeys(block.attrs).map(key => byKey.get(key)).filter(input => !!input && service.inputs.includes(input));
        if (own) used.add(block);
        needed.forEach(input => used.add(input!));
        if ((own || needed.length) && moduleOf.has(block)) modules.add(moduleOf.get(block)!);
      }
      const target = productOf.get(service.anchor);
      if (!used.size || target === product.anchor) continue;
      service.products.add(product.name);
      links.push({ product: product.anchor, service: target ?? service.anchor, modules: [...modules], count: used.size, state: rollup([...used]), ...(target && { internal: true as const }) });
    }
  }
  return { products, services: [...services.values()].filter(service => !productOf.has(service.anchor)).map(service => ({ ...service, products: [...service.products] })), links };
}

/**
 * The order's own requirements (contract, schedule, accesses…): the page's first section when it holds no module and products follow
 * it. No system of the architecture: its card is the board's summary (DeploymentBoardCard), the whole page's progress over its tags.
 */
export const orderSection = (map: DeploymentMap) => (map.products.length > 1 && !map.products[0]!.categories.length ? map.products[0] : undefined);

/**
 * What a board's product and service cards show, as requirement nodes (the requirements overview's `boardNodes`, overviewKinds.ts): the
 * blocks of each card's product or the inputs of its service, from the card's page as `load` reads it (null: a page the reader cannot
 * read adds nothing); each block of a page once, however many cards show it. Flat: a nested block counts, without its parent.
 */
export async function cardRequirementNodes(items: readonly { kind: string; targetResourceId?: string | null; props?: Readonly<Record<string, unknown>> | null }[],
  load: (pageId: string) => Promise<DeploymentDocument | null>): Promise<OverviewJson[]> {
  const cards = items.filter(item => item.kind === "page" && item.targetResourceId && typeof item.props?.anchor === "string"
    && (item.props.display === "product" || item.props.display === "service"));
  const pages = new Map<string, typeof cards>();
  for (const card of cards) pages.set(card.targetResourceId!, [...pages.get(card.targetResourceId!) ?? [], card]);
  const parts = await Promise.all([...pages].map(async ([pageId, shown]): Promise<OverviewJson[]> => {
    const document = await load(pageId);
    if (!document) return [];
    const map = deploymentMap(document), blocks = new Map<string, DeploymentBlock>();
    for (const card of shown) {
      const found = card.props!.display === "product" ? map.products.find(product => product.anchor === card.props!.anchor)?.blocks
        : map.services.find(service => service.anchor === card.props!.anchor)?.inputs;
      found?.forEach(block => blocks.set(block.id, block));
    }
    return [...blocks.values()].map(block => ({ type: "requirement", attrs: block.attrs as unknown as Record<string, unknown>,
      content: [{ type: "paragraph", content: block.title ? [{ type: "text", text: block.title }] : [] }] }));
  }));
  return parts.flat();
}

/** What a board item's text may hold (BoardEditing.MaxText). */
const maxBoardText = 4000;
/** „NIA, NRZP · 3 pož.“: the connector's label, cut to what a board item's text may hold (BoardEditing.MaxText). */
export const linkLabel = (link: DeploymentLink) => [link.modules.join(", "), `${link.count} pož.`].filter(Boolean).join(" · ").slice(0, maxBoardText);

/** What „Poskládat tabuli“ reads of a board's items: the shape of BoardItemDto it needs. */
export interface ComposeItem {
  readonly id: string; readonly kind: string; readonly x: number; readonly y: number; readonly width: number; readonly height: number; readonly z: number;
  readonly props?: BoardProps | null; readonly target?: { readonly resourceId: string } | null; readonly from?: string | null; readonly to?: string | null;
  /** A connector's label: the one compose wrote tells its own connectors from a person's (arrangeChanges). */
  readonly text?: string | null;
}

const PRODUCT_WIDTH = 400, SERVICE_WIDTH = 300, SERVICE_HEIGHT = 150, GAP = 32, BELOW = 160, LANE = 120, STAGGER = 56;
const TAG_ROW = 360, TAG_MAX = 220, TAG_GAP = 4;
/** The rows a product's tags wrap into (DeploymentBoardCard's .dcard-tag): about 6.4 px a character and its padding, at most 220 px a tag. */
export function tagRows(blocks: readonly DeploymentBlock[]) {
  let rows = 0, used = TAG_ROW;
  for (const block of blocks) {
    const width = Math.min(TAG_MAX, 18 + 6.4 * (block.title || block.attrs.key).length);
    if (used + TAG_GAP + width > TAG_ROW) { rows++; used = width; } else used += TAG_GAP + width;
  }
  return rows;
}
/** A product card as tall as its tags, categories and modules (the card scrolls inside when a person makes it smaller). */
export const productHeight = (product: DeploymentProduct) =>
  Math.min(1600, 84 + (product.base.length ? 14 + 26 * tagRows(product.base) : 0) + product.categories.length * 44 + product.categories.reduce((sum, category) => sum + category.modules.length * 36, 0));
const edgeStates: Partial<Record<RequirementState, BoardProps["state"]>> = { done: "done", waiting_for_input: "waiting", blocked: "blocked" };

type CardDisplay = "product" | "service";
interface CardSize { readonly width: number; readonly height: number }
interface PlacedCard extends CardSize { readonly display: CardDisplay; readonly anchor: string; readonly x: number; readonly y: number }

/**
 * The architecture picture, the one placement „Tabule nasazení“ (composeChanges) and „Uspořádat“ (arrangeChanges) share: the products
 * side by side from `left` in the page's order (the order's own section first, so top left), each centred over a row of the external
 * services it is the first to use; every other service of the row stands a step lower, so the labels of the connectors fanning out of
 * a product do not cover each other. `sizeOf` says which cards are placed and how large each is (undefined: not placed). `below`
 * names the product>service pairs that stand one under the other: their connector runs straight down.
 */
function lanes(map: DeploymentMap, left: number, top: number, sizeOf: (display: CardDisplay, anchor: string) => CardSize | undefined) {
  const cards: PlacedCard[] = [], below = new Set<string>(), taken = new Set<string>();
  let x = left;
  for (const product of map.products) {
    const own = sizeOf("product", product.anchor);
    const services = map.links.filter(link => !link.internal && link.product === product.anchor && !taken.has(link.service))
      .flatMap(link => { const size = sizeOf("service", link.service); return size ? [{ anchor: link.service, width: size.width, height: size.height }] : []; });
    const row = services.reduce((sum, service) => sum + service.width + GAP, -GAP), lane = Math.max(own?.width ?? 0, row);
    if (lane <= 0) continue;
    if (own) cards.push({ display: "product", anchor: product.anchor, x: x + (lane - own.width) / 2, y: top, width: own.width, height: own.height });
    let at = x + (lane - row) / 2;
    services.forEach((service, index) => {
      taken.add(service.anchor);
      cards.push({ display: "service", ...service, x: at, y: top + (own ? own.height + BELOW : 0) + (index % 2) * STAGGER });
      at += service.width + GAP;
      if (own) below.add(`${product.anchor}>${service.anchor}`);
    });
    x += lane + LANE;
  }
  return { cards, below };
}
const isCard = (item: ComposeItem, pageId: string) => item.kind === "page" && item.target?.resourceId === pageId && (item.props?.display === "product" || item.props?.display === "service");
const cardKey = (display: unknown, anchor: unknown) => `${String(display)}:${String(anchor)}`;

/**
 * The changes that lay the page's deployment out on a board as an architecture picture (lanes): a connector from a product to each
 * service it uses with „NIA, NRZP · 3 pož.“ and one between two products when one's `system` names the other. Only what the board
 * lacks: whatever is there (cards a person moved or resized, their own arrows and items) stays untouched, new cards go in a row under
 * everything the board holds; a connector only between cards that have none. Cards come before their connectors.
 */
export function composeChanges(map: DeploymentMap, pageId: string, items: readonly ComposeItem[], newId: () => string): BoardChange[] {
  const changes: BoardChange[] = [];
  let z = items.reduce((top, item) => Math.max(top, item.z), 0);
  const ids = new Map(items.filter(item => isCard(item, pageId)).map(card => [cardKey(card.props!.display, card.props!.anchor), card.id]));
  const placed = items.filter(item => item.kind !== "connector"), heights = new Map(map.products.map(product => [product.anchor, productHeight(product)]));
  const top = placed.reduce((bottom, item) => Math.max(bottom, item.y + item.height + GAP), 0);
  const { cards, below } = lanes(map, placed.length ? Math.min(...placed.map(item => item.x)) : 0, top, (display, anchor) => (ids.has(cardKey(display, anchor)) ? undefined
    : display === "product" ? { width: PRODUCT_WIDTH, height: heights.get(anchor)! } : { width: SERVICE_WIDTH, height: SERVICE_HEIGHT }));
  for (const { display, anchor, ...geometry } of cards) {
    const id = newId();
    ids.set(cardKey(display, anchor), id);
    changes.push({ type: "create", item: { id, kind: "page", ...geometry, z: ++z, targetResourceId: pageId, props: { display, anchor } } });
  }
  const connected = new Set(items.filter(item => item.kind === "connector").map(item => `${item.from}>${item.to}`));
  for (const link of map.links) {
    const from = ids.get(cardKey("product", link.product))!, to = ids.get(cardKey(link.internal ? "product" : "service", link.service))!;
    if (connected.has(`${from}>${to}`)) continue;
    const state = edgeStates[link.state];
    // Without sides a connector takes the two that face each other, wherever its cards are moved (boardFlow.autoSides).
    const sides = !link.internal && below.has(`${link.product}>${link.service}`) ? { fromSide: "bottom" as const, toSide: "top" as const } : {};
    changes.push({ type: "create", item: { id: newId(), kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to, text: linkLabel(link),
      props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: "solid", ...sides, ...(state && { state }) } } });
  }
  return changes;
}

/**
 * „Uspořádat“ of a deployment board (owner, 4.0): the product and service cards of `pages` that are on the board (those of `ids`, or all
 * of them when none is given) moved into the architecture picture a new board is composed in (lanes), each at the size it has, the
 * pages' pictures one under another in the order given. The picture keeps the top left corner of where the cards stand now, unless a
 * card would then cover an item that stays (a person's note or shape, a card of another page, one out of the selection; a frame or
 * group may hold it): then the whole picture goes under everything that stays. The connectors compose made between two arranged cards
 * (a link of the page between those two cards, in its direction and with its label) face each other again: straight down from a
 * product to a service under it, the facing sides (none stored) between others. Nothing else moves or changes: other items, a
 * person's own arrows, whatever sides they have, a card whose product or service the page no longer has. One batch, so one step to undo.
 */
export function arrangeChanges(pages: readonly (readonly [pageId: string, map: DeploymentMap])[], items: readonly ComposeItem[], ids: readonly string[] = []): BoardChange[] {
  const sets = pages.map(([pageId, map]) => ({ map, byKey: new Map(items.filter(item => isCard(item, pageId) && (!ids.length || ids.includes(item.id)))
    .map(card => [cardKey(card.props!.display, card.props!.anchor), card])) })).filter(set => set.byKey.size);
  const all = sets.flatMap(set => [...set.byKey.values()]);
  if (!all.length) return [];
  const left = Math.min(...all.map(card => card.x));
  const lay = (top: number) => sets.map(set => {
    const picture = lanes(set.map, left, top, (display, anchor) => set.byKey.get(cardKey(display, anchor)));
    top = Math.max(top, ...picture.cards.map(card => card.y + card.height + LANE));
    return { ...set, ...picture, placed: picture.cards.map(next => ({ next, card: set.byKey.get(cardKey(next.display, next.anchor))! })) };
  });
  let pictures = lay(Math.min(...all.map(card => card.y)));
  const moving = new Set(pictures.flatMap(picture => picture.placed.map(({ card }) => card.id)));
  const staying = items.filter(item => item.kind !== "connector" && item.kind !== "frame" && item.kind !== "group" && !moving.has(item.id));
  const covers = (a: PlacedCard, b: ComposeItem) => a.x < b.x + b.width + GAP && b.x < a.x + a.width + GAP && a.y < b.y + b.height + GAP && b.y < a.y + a.height + GAP;
  if (pictures.some(picture => picture.cards.some(next => staying.some(other => covers(next, other))))) pictures = lay(Math.max(...staying.map(item => item.y + item.height)) + GAP);
  const changes: BoardChange[] = [];
  for (const { map, byKey, placed, below } of pictures) {
    for (const { next, card } of placed) if (card.x !== next.x || card.y !== next.y) changes.push({ type: "move", itemId: card.id, x: next.x, y: next.y, width: card.width, height: card.height });
    const arranged = new Set(placed.map(({ card }) => card.id));
    for (const link of map.links) {
      const from = byKey.get(cardKey("product", link.product))?.id, to = byKey.get(cardKey(link.internal ? "product" : "service", link.service))?.id;
      if (!from || !to || !arranged.has(from) || !arranged.has(to)) continue;
      const down = !link.internal && below.has(`${link.product}>${link.service}`), fromSide = down ? "bottom" : null, toSide = down ? "top" : null;
      for (const line of items) {
        if (line.kind !== "connector" || line.from !== from || line.to !== to || (line.text ?? "") !== linkLabel(link)) continue;
        if ((line.props?.fromSide ?? null) !== fromSide || (line.props?.toSide ?? null) !== toSide) changes.push({ type: "set", itemId: line.id, props: { fromSide, toSide } });
      }
    }
  }
  return changes;
}
