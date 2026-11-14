// @vitest-environment node
import { describe, expect, it } from "vitest";
// Deliberately imported through the `@/` alias as well as relatively: generated
// shadcn components import each other that way, and the alias has to hold in
// Vite, in Vitest and in vue-tsc (where `extends` replaces `paths` wholesale).
import { cn as cnViaAlias } from "@/lib/utils";
import { cn } from "./utils";

describe("cn", () => {
  it("joins classes", () => {
    expect(cn("a", "b")).toBe("a b");
  });

  it("drops falsy values", () => {
    expect(cn("a", false && "b", undefined, null, "c")).toBe("a c");
  });

  it("lets a later conflicting utility win", () => {
    // The whole reason generated components use this rather than a plain join:
    // a caller's `class` must be able to override the component's own default.
    expect(cn("p-2", "p-4")).toBe("p-4");
  });

  it("keeps the app's own non-Tailwind class names intact", () => {
    // Call sites still pass their own layout classes alongside a variant, and
    // tailwind-merge must not treat those as conflicting utilities and drop one.
    expect(cn("nav-item", "primary")).toBe("nav-item primary");
  });

  it("is reachable through the @/ alias generated components use", () => {
    expect(cnViaAlias).toBe(cn);
  });
});
