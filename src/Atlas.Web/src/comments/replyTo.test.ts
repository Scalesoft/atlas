import { describe, expect, it } from "vitest";
import { replyTo } from "./pageDiscussionFeed";

describe("replyTo", () => {
  it("starts a quick reply with a mention of the bubble's author (the account the server notifies)", () => {
    const text = replyTo({ authorId: "00000000-0000-0000-0000-0000000000b2", authorName: "Petr [Pe] Svoboda" }, "Souhlasím");
    expect(text).toBe("[Petr \\[Pe\\] Svoboda](a-contact:?name=Petr%20%5BPe%5D%20Svoboda&internalUser.id=00000000-0000-0000-0000-0000000000b2&internalUser.name=Petr%20%5BPe%5D%20Svoboda) Souhlasím");
  });
});
