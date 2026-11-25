import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import type { DiscussionBubble } from "../../comments/pageDiscussionFeed";
import PageDiscussionPeek from "./PageDiscussionPeek.vue";

let wrapper: VueWrapper | undefined;
beforeEach(() => vi.useFakeTimers());
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); document.body.replaceChildren(); });

const bubble = (id: string, text = `Text ${id}`): DiscussionBubble => ({ id, authorId: "00000000-0000-0000-0000-0000000000b2", authorName: "Petr Svoboda", text });
const mounted = (bubbles: DiscussionBubble[], props: Record<string, unknown> = {}) => (wrapper = mount(PageDiscussionPeek, { props: { bubbles, ...props }, attachTo: document.body }));

describe("PageDiscussionPeek", () => {
  it("shows a colleague's comment as a bubble with the author, the words and a reply that opens the discussion", async () => {
    const view = mounted([bubble("a", "Souhlasím, čtvrtek je lepší")]);
    expect(view.get("[data-discussion-peek]").attributes("role")).toBe("log");
    expect(view.get("[data-peek-bubble]").text()).toContain("Petr Svoboda");
    expect(view.get("[data-peek-bubble]").text()).toContain("Souhlasím, čtvrtek je lepší");
    expect(view.get(".peek-open").attributes("aria-label")).toBe("Petr Svoboda: Souhlasím, čtvrtek je lepší");
    await view.get(".peek-open").trigger("click");
    expect(view.emitted("open")).toHaveLength(1);
  });

  it("lets a bubble go after its time, each on its own, and again when the pointer leaves it (a bubble pointed at stays)", async () => {
    const view = mounted([bubble("a"), bubble("b")], { ttl: 1000 });
    await vi.advanceTimersByTimeAsync(1000);
    expect(view.emitted("dismiss")?.map(args => args[0])).toEqual(["a", "b"]);

    const held = mounted([bubble("c")], { ttl: 1000 });
    await held.get("[data-discussion-peek]").trigger("pointerenter");
    await vi.advanceTimersByTimeAsync(5000);
    expect(held.emitted("dismiss")).toBeUndefined();
    await held.get("[data-discussion-peek]").trigger("pointerleave");
    await vi.advanceTimersByTimeAsync(1000);
    expect(held.emitted("dismiss")?.[0]).toEqual(["c"]);
  });

  it("dismisses a bubble by its button, schedules one that arrives later, and says it docks while the discussion opens", async () => {
    const view = mounted([bubble("a")], { ttl: 1000 });
    await view.get(".peek-hide").trigger("click");
    expect(view.emitted("dismiss")?.[0]).toEqual(["a"]);
    await view.setProps({ bubbles: [bubble("a"), bubble("n")] });
    await vi.advanceTimersByTimeAsync(1000);
    expect(view.emitted("dismiss")?.map(args => args[0])).toContain("n");
    expect(view.get("[data-discussion-peek]").attributes("data-docked")).toBeUndefined();
    await view.setProps({ docked: true, bubbles: [] });
    expect(view.get("[data-discussion-peek]").attributes("data-docked")).toBeDefined();
  });
});
