import { afterEach, describe, expect, it, vi } from "vitest";

const sonner = vi.hoisted(() => ({ toast: Object.assign(vi.fn(), { dismiss: vi.fn() }) }));
vi.mock("vue-sonner", () => sonner);

import { releaseLabel, releaseLine, releaseOf, showWhatsNew, whatsNewNotice } from "./notice";

afterEach(() => { sonner.toast.mockReset(); sonner.toast.dismiss.mockReset(); document.body.innerHTML = ""; });

describe("Co je nového: when the notice shows", () => {
  it("shows a release the account has not seen, once, and never for a development build", () => {
    expect(whatsNewNotice("3.5.0+abc1234", "3.4.3")).toBe("show");
    expect(whatsNewNotice("3.5.0+abc1234", "3.5.0")).toBe("none");
    // A new account has seen nothing: it takes the running release without the notice.
    expect(whatsNewNotice("3.5.0+abc1234", null)).toBe("adopt");
    expect(whatsNewNotice("3.5.0", undefined)).toBe("adopt");
    for (const build of ["3.5.0-dev+abc1234", "3.5.0-dev", "unknown", undefined]) expect(whatsNewNotice(build, "3.4.3"), build).toBe("none");
  });

  it("reads the release from the server's version and names it shortly", () => {
    expect(releaseOf("3.10.2+abc")).toBe("3.10.2");
    expect(releaseOf("3.5.0-dev+abc")).toBeUndefined();
    expect(releaseLabel("3.5.0")).toBe("3.5");
    expect(releaseLabel("3.4.3")).toBe("3.4.3");
  });
});

describe("Co je nového: the notice", () => {
  const options = () => sonner.toast.mock.calls[0]![1] as { action: { onClick: () => void }; onDismiss: () => void; position: string; duration: number; class: string };

  it("waits at the top until opened or closed, and records the release once whichever way it closes", () => {
    const open = vi.fn(), seen = vi.fn();
    showWhatsNew("3.5.0", { open, seen });
    expect(sonner.toast.mock.calls[0]![0]).toBe("Atlas 3.5 — co je nového");
    expect(options().position).toBe("top-center");
    expect(options().duration).toBe(Number.POSITIVE_INFINITY);
    options().action.onClick();
    options().onDismiss();
    expect(open).toHaveBeenCalledOnce();
    expect(seen).toHaveBeenCalledOnce();
  });

  it("names a release by its codename with the mark as decoration, and closes once when the page opens another way", () => {
    const seen = vi.fn();
    const close = showWhatsNew("3.5.0", { open: vi.fn(), seen }, { codename: "Performant Pear", mark: "pear" });
    expect(sonner.toast.mock.calls[0]![0]).toBe("Atlas 3.5 Performant Pear — co je nového");
    const icon = (options() as unknown as { icon: () => { props: Record<string, unknown> } }).icon();
    expect(icon.props).toEqual({ mark: "pear", variant: "icon" });
    close();
    close();
    options().onDismiss();
    expect(sonner.toast.dismiss).toHaveBeenCalledOnce();
    expect(seen).toHaveBeenCalledOnce();
  });

  it("presents a patch after one of the same version as a small update of the named version, else as the named version", () => {
    const pear = { codename: "Performant Pear", mark: "pear" };
    showWhatsNew("3.5.2", { open: vi.fn(), seen: vi.fn() }, pear, "3.5.0");
    expect(sonner.toast.mock.calls[0]![0]).toBe("Atlas 3.5.2 — opravy verze 3.5 Performant Pear");
    expect((options() as unknown as { description: string }).description).toBe("Drobné opravy verze, kterou už znáte.");
    // From an older version the account meets the named version first, never the patch as an equal of it.
    showWhatsNew("3.5.2", { open: vi.fn(), seen: vi.fn() }, pear, "3.4.3");
    expect(sonner.toast.mock.calls[1]![0]).toBe("Atlas 3.5 Performant Pear — co je nového");
    expect(releaseLine("3.5.2")).toBe("3.5");
  });

  it("closes on Esc only while the keyboard is in it", () => {
    const seen = vi.fn();
    showWhatsNew("3.5.0", { open: vi.fn(), seen });
    const outside = document.body.appendChild(document.createElement("input"));
    outside.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(seen).not.toHaveBeenCalled();
    const main = document.body.appendChild(document.createElement("main"));
    main.tabIndex = -1;
    const toast = document.body.appendChild(document.createElement("li"));
    toast.className = options().class;
    toast.tabIndex = 0;
    toast.focus();
    toast.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(sonner.toast.dismiss).toHaveBeenCalledWith("whats-new");
    expect(seen).toHaveBeenCalledOnce();
    // The keyboard is not left on the body the removed toast leaves.
    expect(document.activeElement).toBe(main);
  });
});
