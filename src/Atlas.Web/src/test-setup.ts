import { config } from "@vue/test-utils";
import { vi } from "vitest";
// The tool speaks Czech in tests (jsdom's browser is en-US, node's navigator follows the machine: English on the CI runner);
// a test of English sets the cookie and imports afresh (src/i18n).
Object.defineProperty(navigator, "language", { value: "cs-CZ", configurable: true });
// Files that never touch the DOM run in the node environment (`// @vitest-environment node`), which has no window.
if (typeof window !== "undefined") {
  Object.defineProperty(window, "matchMedia", {
    writable: true,
    value: vi
      .fn()
      .mockImplementation((query) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
  });
  if (!globalThis.ResizeObserver)
    globalThis.ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
  if (!globalThis.requestAnimationFrame)
    globalThis.requestAnimationFrame = (callback) =>
      setTimeout(() => callback(Date.now()), 0) as unknown as number;
  if (!globalThis.cancelAnimationFrame)
    globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
  if (!HTMLElement.prototype.scrollIntoView)
    HTMLElement.prototype.scrollIntoView = () => {};
  if (!Range.prototype.getBoundingClientRect)
    Range.prototype.getBoundingClientRect = () => new DOMRect();
  if (!Range.prototype.getClientRects)
    Range.prototype.getClientRects = () => [] as unknown as DOMRectList;
  if (!HTMLDialogElement.prototype.showModal)
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute("open", "");
    };
  if (!HTMLDialogElement.prototype.close)
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute("open");
      this.dispatchEvent(new Event("close"));
    };
  
}

// reka-ui's TooltipRoot throws without a TooltipProvider ancestor, and the
// provider lives once at the application root -- shadcn's own arrangement, and
// where the shared reopen delay belongs. A component test mounts one component
// rather than the app, so it has no such ancestor.
//
// Tooltips are not what those tests are about, so they render as pass-throughs:
// the trigger's own element and attributes stay exactly as the component wrote
// them, which is what the assertions look at. The tooltip's real behaviour is
// covered by src/components/ui/tooltip/tooltip.test.ts, which supplies a
// provider explicitly.
config.global.stubs = {
  ...config.global.stubs,
  Tooltip: { template: "<slot />" },
  TooltipTrigger: { template: "<slot />" },
  TooltipProvider: { template: "<slot />" },
  // Renders nothing: a tooltip's text is not part of the trigger's own text,
  // and including it would double every label the assertions read.
  TooltipContent: { template: "<span />" },
};
