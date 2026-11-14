// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { Extension } from "@tiptap/core";
import { Plugin } from "@tiptap/pm/state";
import type { MarkdownPlugin } from "@atlas/markdown/core";
import { createLiveEditorPlugins } from "./liveEditorPlugins";
import type { AtlasAssistContext, AtlasAssistFactory } from "./types";

describe("createLiveEditorPlugins", () => {
  it("attaches an assist that arrives after the editor opened, in place: its plugins, hooks and actions-menu rows", () => {
    const plugin = new Plugin({}), onCreate = vi.fn(), onAttach = vi.fn(), rows = [{ id: "assist.link", label: "Propojit" }];
    const assist: AtlasAssistFactory = () => ({ id: "assist", apiVersion: 1, create: () => ({
      extensions: [Extension.create({ name: "late", addProseMirrorPlugins: () => [plugin] })], onCreate, onAttach, intentions: () => rows as never,
    }) }) as MarkdownPlugin;
    let factory: AtlasAssistFactory | null = null;
    const live = createLiveEditorPlugins([], () => factory, {} as AtlasAssistContext);
    const instance = live.for("a")[0]!.create({ profile: {} as never, signal: new AbortController().signal });
    const editor = { registerPlugin: vi.fn() }, context = { editor, controller: {}, signal: new AbortController().signal } as never;
    instance.onCreate!(context);
    instance.onAttach!(context);
    expect(instance.intentions!({} as never)).toEqual([]);
    live.arrived(factory = assist);
    expect(editor.registerPlugin).toHaveBeenCalledWith(plugin);
    expect(onCreate).toHaveBeenCalledWith(context);
    expect(onAttach).toHaveBeenCalledWith(context);
    expect(instance.intentions!({} as never)).toBe(rows);
    // An instance built with the factory here gets the plugin itself, the same list on every render.
    expect(live.for("b")).toBe(live.for("b"));
  });
});
