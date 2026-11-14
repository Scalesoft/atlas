import { describe, expect, it, vi } from "vitest";
import { assistantCommands, assistantContextFor, revealCitedHeading, wikiTasks } from "./assistantEntries";

const page = { id: "p1", title: "VPN", spaceId: "s1", isPublished: true };
const space = { id: "s1", name: "IT" };

describe("assistant entries", () => {
  it("puts the page into the context only while a published page is being read", () => {
    expect(assistantContextFor({ documentOpen: true, editing: false, page, space })).toEqual({ pageId: "p1", pageTitle: "VPN", spaceId: "s1", spaceName: "IT", canWrite: false });
    expect(assistantContextFor({ documentOpen: true, editing: true, page, space }).pageId).toBeNull();
    expect(assistantContextFor({ documentOpen: true, editing: false, page: { ...page, isPublished: false }, space }).pageId).toBeNull();
    expect(assistantContextFor({ documentOpen: false, editing: false, page, space })).toMatchObject({ pageId: null, spaceId: "s1" });
    expect(assistantContextFor({ documentOpen: false, editing: false, page: null, space: null })).toEqual({ pageId: null, pageTitle: null, spaceId: null, spaceName: null });
  });

  it("offers the commands only with the capability, and the page one only on a page", () => {
    const open = vi.fn();
    const onPage = assistantContextFor({ documentOpen: true, editing: false, page, space });
    expect(assistantCommands(false, onPage, open)).toEqual([]);
    expect(assistantCommands(true, assistantContextFor({ documentOpen: false, editing: false, page: null, space }), open).map((c) => c.label)).toEqual(["Asistent"]);
    const commands = assistantCommands(true, onPage, open);
    expect(commands.map((c) => [c.label, c.icon])).toEqual([["Asistent", "ai"], ["Zeptat se na tuto stránku", "ai"]]);
    commands[1]!.run();
    expect(open).toHaveBeenCalledWith(onPage, true);
    // No question can be answered now (the agent alone with „S nástroji“ off): the panel opener stays, the ask over the page goes.
    expect(assistantCommands(true, onPage, open, false, false, false).map((c) => c.id)).toEqual(["assistant"]);
  });

  it("offers wiki tasks to writers only, page tasks only on a read page, as palette commands only with the agent", () => {
    const reader = assistantContextFor({ documentOpen: true, editing: false, page, space });
    const writer = assistantContextFor({ documentOpen: true, editing: false, page, space, canWrite: true });
    const inSpace = assistantContextFor({ documentOpen: false, editing: false, page: null, space, canWrite: true });
    expect(wikiTasks(reader)).toEqual([]);
    expect(wikiTasks(writer, true).map((task) => task.scope)).toEqual(["page", "page", "space", "space", "space"]);
    expect(wikiTasks(inSpace, true).map((task) => task.id)).toEqual(["task-space-icons", "task-space-tags", "task-space-menu"]);
    // The admin switched the batch move off: no menu task.
    expect(wikiTasks(inSpace).map((task) => task.id)).toEqual(["task-space-icons", "task-space-tags"]);
    expect(wikiTasks(inSpace)[0]!.prompt).toContain("v prostoru IT");
    expect(wikiTasks(assistantContextFor({ documentOpen: false, editing: false, page: null, space: null, canWrite: true }))).toEqual([]);

    const open = vi.fn();
    expect(assistantCommands(true, writer, open).map((c) => c.id)).toEqual(["assistant", "ask-page"]);
    expect(assistantCommands(true, writer, open, true, true).map((c) => c.id)).toEqual(["assistant", "ask-page", "task-contacts", "task-page-labels", "task-space-icons", "task-space-tags", "task-space-menu"]);
    expect(assistantCommands(true, reader, open, true).map((c) => c.id)).toEqual(["assistant", "ask-page"]);
  });

  it("lights up the cited heading once it exists and gives up quietly otherwise", async () => {
    document.body.innerHTML = `<div class="document-body"><h2 data-heading-id="profil">Profil</h2></div>`;
    const heading = document.querySelector("h2")!;
    heading.scrollIntoView = vi.fn();
    heading.animate = vi.fn(() => ({ finished: Promise.resolve() })) as never;
    expect(await revealCitedHeading("profil")).toBe(true);
    expect(heading.scrollIntoView).toHaveBeenCalled();
    expect(heading.animate).toHaveBeenCalled();
    expect(await revealCitedHeading(null)).toBe(false);
    document.body.innerHTML = "";
  });
});
