// @vitest-environment node
import { describe, expect, it } from "vitest";
import { computed, effectScope, ref } from "vue";
import { only, perItem } from "./boardCanvasContext";

describe("perItem", () => {
  it("wakes only the items whose entry changed, and an item read later starts at the current state", () => {
    const scope = effectScope();
    scope.run(() => {
      const single = ref<string>();
      const isSingle = perItem(() => only(single.value));
      let runsA = 0, runsB = 0;
      const a = computed(() => { runsA++; return isSingle("a"); }), b = computed(() => { runsB++; return isSingle("b"); });
      expect([a.value, b.value]).toEqual([undefined, undefined]);
      single.value = "a";
      expect([a.value, b.value]).toEqual([true, undefined]);
      expect([runsA, runsB]).toEqual([2, 1]);
      single.value = "c";
      expect([a.value, b.value]).toEqual([undefined, undefined]);
      expect([runsA, runsB]).toEqual([3, 1]);
      expect(isSingle("c")).toBe(true);
    });
    scope.stop();
  });
});
