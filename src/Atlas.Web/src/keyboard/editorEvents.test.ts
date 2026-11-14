// @vitest-environment node
import { describe, expect, it } from "vitest";
import * as core from "@atlas/markdown/core";
import { MARKDOWN_LEAVE_START_EVENT, MARKDOWN_SHORTCUT_HELP_EVENT } from "./editorEvents";

describe("editorEvents", () => {
  it("names the same events as the editor core", () => {
    expect(MARKDOWN_SHORTCUT_HELP_EVENT).toBe(core.MARKDOWN_SHORTCUT_HELP_EVENT);
    expect(MARKDOWN_LEAVE_START_EVENT).toBe(core.MARKDOWN_LEAVE_START_EVENT);
  });
});
