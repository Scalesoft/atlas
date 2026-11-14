import { expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import * as treeMove from "../treeMove";
import type { TreePage } from "../treeMove";
import PageTree from "./PageTree.vue";

vi.mock("../treeMove", async original => {
  const actual = await original<typeof import("../treeMove")>();
  return { ...actual, visibleTreeOrder: vi.fn(actual.visibleTreeOrder) };
});

const node = (id: string, parentId: string | null = null): TreePage => ({ id, parentId, spaceId: "space", title: id, version: 1, isPublished: true, canEdit: true });

it("derives the visible order once for the whole tree, however many levels are expanded", async () => {
  // A chain of 30 expanded levels renders 30 nested PageTree instances.
  const pages = Array.from({ length: 30 }, (_, index) => node(`page-${index}`, index ? `page-${index - 1}` : null));
  const app = mount(PageTree, { props: { pages } });
  expect(app.findAll('[role="treeitem"]')).toHaveLength(30);
  expect(app.findAll('[tabindex="0"]')).toHaveLength(1);
  expect(treeMove.visibleTreeOrder).toHaveBeenCalledTimes(1);
  await app.setProps({ pages: [...pages, node("sibling")] });
  expect(app.findAll('[role="treeitem"]')).toHaveLength(31);
  expect(treeMove.visibleTreeOrder).toHaveBeenCalledTimes(2);
  app.unmount();
});
