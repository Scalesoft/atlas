import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { Editor } from "@tiptap/core";
import type { DiscussionBubble } from "../../comments/pageDiscussionFeed";
import { drafts } from "../../lib/unsavedWork";
import PageDiscussionPeek from "./PageDiscussionPeek.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); drafts.clear(); });

const bubble: DiscussionBubble = { id: "a", authorId: "00000000-0000-0000-0000-0000000000b2", authorName: "Petr Svoboda", text: "Čtvrtek je lepší" };
const editor = () => document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
const enter = (shiftKey = false) => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", shiftKey, bubbles: true, cancelable: true }));

async function startReply(reply: (markdown: string, bubble: DiscussionBubble) => Promise<void>) {
  wrapper = mount(PageDiscussionPeek, { props: { bubbles: [bubble], reply, ttl: 100_000 }, attachTo: document.body });
  await wrapper.get(".peek-reply").trigger("click");
  await vi.waitFor(() => expect(editor()).toBeDefined());
  return wrapper;
}

describe("PageDiscussionPeek quick reply", () => {
  it("offers Odpovědět only when the host can post, and opens the comment field with the paper plane dim until there is text", async () => {
    wrapper = mount(PageDiscussionPeek, { props: { bubbles: [bubble] }, attachTo: document.body });
    expect(wrapper.find(".peek-reply").exists()).toBe(false);
    wrapper.unmount();

    const view = await startReply(vi.fn(async () => undefined));
    const send = view.get("[data-peek-reply] .send-button");
    expect(send.attributes("aria-label")).toBe("Odeslat (Enter)");
    expect(send.attributes("aria-disabled")).toBe("true");
    expect(send.attributes("data-ready")).toBeUndefined();
    editor()!.commands.insertContent("Souhlasím");
    await flushPromises();
    expect(send.attributes("aria-disabled")).toBeUndefined();
    expect(send.attributes("data-ready")).toBeDefined();
  });

  it("sends with Enter (not Shift+Enter, not an empty field), then the bubble goes and the keyboard is on the discussion button", async () => {
    const reply = vi.fn(async () => undefined);
    const button = document.body.appendChild(Object.assign(document.createElement("button"), { type: "button" }));
    button.setAttribute("data-page-discussion", "");
    const view = await startReply(reply);
    enter();
    await flushPromises();
    expect(reply).not.toHaveBeenCalled();
    editor()!.commands.insertContent("Souhlasím");
    await flushPromises();
    enter(true);
    await flushPromises();
    expect(reply).not.toHaveBeenCalled();
    enter();
    await flushPromises();
    expect(reply).toHaveBeenCalledWith("Souhlasím", bubble);
    expect(view.emitted("dismiss")?.[0]).toEqual(["a"]);
    expect(view.get("[data-peek-bubble]").attributes("data-sent")).toBeDefined();
    expect(document.activeElement).toBe(button);
  });

  it("sends with the paper-plane button too, and keeps the bubble and the text when the send fails", async () => {
    const reply = vi.fn(async () => { throw new Error("Nepodařilo se odeslat"); });
    const view = await startReply(reply);
    editor()!.commands.insertContent("Souhlasím");
    await flushPromises();
    await view.get("[data-peek-reply] .send-button").trigger("click");
    await flushPromises();
    expect(reply).toHaveBeenCalledTimes(1);
    expect(view.emitted("dismiss")).toBeUndefined();
    expect(view.get("[data-peek-reply] [role=alert]").text()).toContain("Nepodařilo se odeslat");
    expect(editor()!.getText()).toBe("Souhlasím");
  });

  it("folds the reply with Escape and keeps what was typed as the page's comment draft", async () => {
    wrapper = mount(PageDiscussionPeek, { props: { bubbles: [bubble], reply: vi.fn(async () => undefined), draftScope: "P1", ttl: 100_000 }, attachTo: document.body });
    await wrapper.get(".peek-reply").trigger("click");
    await vi.waitFor(() => expect(editor()).toBeDefined());
    editor()!.commands.insertContent("Napůl napsané");
    await flushPromises();
    document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(document.querySelector(".ProseMirror")).toBeNull());
    expect(wrapper.emitted("replying")!.at(-1)).toEqual(["a", false]);
    expect([...drafts.keys()]).toEqual(["comment:p1:"]);
  });

  it("keeps an unsent reply as a draft of the page when the bubble goes, and drops it once sent", async () => {
    wrapper = mount(PageDiscussionPeek, { props: { bubbles: [bubble], reply: vi.fn(async () => undefined), draftScope: "p1", ttl: 100_000 }, attachTo: document.body });
    const view = wrapper;
    await view.get(".peek-reply").trigger("click");
    await vi.waitFor(() => expect(editor()).toBeDefined());
    editor()!.commands.insertContent("Napůl napsané");
    await flushPromises();
    expect(view.emitted("replying")![0]).toEqual(["a", true]);
    await view.setProps({ bubbles: [] });
    await flushPromises();
    expect([...drafts.keys()]).toEqual(["comment:p1:"]);
  });
});
