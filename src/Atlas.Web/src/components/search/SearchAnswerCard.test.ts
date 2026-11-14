import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick, ref } from "vue";
import SearchAnswerCard from "./SearchAnswerCard.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; Reflect.deleteProperty(HTMLElement.prototype, "animate"); vi.restoreAllMocks(); });

function fakeAnswer(state: string) {
  return {
    visible: ref(true), state: ref(state), sources: ref([]), message: ref(null), rating: ref(null), preference: ref("auto"), conversationId: ref(null), notice: ref(""),
    stop() {}, ask() {}, feedback() {},
  } as never;
}

it("glides the content to where the finished answer lies instead of moving it in one frame", async () => {
  const animate = vi.fn(() => ({ finished: Promise.resolve(), addEventListener() {}, cancel() {} }));
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  const answer = fakeAnswer("preparing") as { state: { value: string } };
  wrapper = mount(SearchAnswerCard, { props: { answer: answer as never, canContinue: false }, attachTo: document.body });
  await nextTick();
  const main = wrapper.get(".ai-answer-main").element as HTMLElement;
  // The working card has its padding (content at 20/30); the finished answer lies on the pane (content at 4/18).
  let reads = 0;
  vi.spyOn(main, "getBoundingClientRect").mockImplementation(() => (reads++ === 0 ? { x: 20, y: 30 } : { x: 4, y: 18 }) as DOMRect);
  animate.mockClear();
  answer.state.value = "done";
  await nextTick(); await nextTick();
  const call = (animate.mock.calls as unknown as [Keyframe[], KeyframeAnimationOptions][]).find(([frames]) => frames.some((frame) => String(frame.transform).includes("translate")));
  expect(call?.[0][0]?.transform).toBe("translate(16px, 12px)");
  expect(call?.[0][1]?.transform).toBe("none");
});

it("does not move anything when the answer was already finished", async () => {
  const animate = vi.fn(() => ({ finished: Promise.resolve(), addEventListener() {}, cancel() {} }));
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  wrapper = mount(SearchAnswerCard, { props: { answer: fakeAnswer("done"), canContinue: false }, attachTo: document.body });
  await nextTick(); await nextTick();
  expect((animate.mock.calls as unknown as [Keyframe[]][]).some(([frames]) => frames.some((frame) => String(frame.transform).includes("translate")))).toBe(false);
});
