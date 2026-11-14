/**
 * P7 „Po zavření panelu nebo dialogu se klávesnice vrátí tam, odkud byl otevřen“, for a popover or panel that has no trigger of
 * its own (a picker opened from the canvas or a shortcut). Call it when the panel opens; call what it returns when it closes.
 * Without a remembered element (the opener was <body> or is gone) the fallback takes the keyboard, so it never stays on <body>.
 * AppDialog and WorkspaceSidePanel keep their own (a dialog opened from a closing menu item, a dock's tab roving).
 */
export function rememberFocus(fallback?: () => HTMLElement | null | undefined): () => boolean {
  const active = document.activeElement;
  const opener = active instanceof HTMLElement && active !== document.body ? active : null;
  return () => {
    const target = opener?.isConnected ? opener : fallback?.();
    if (!target?.isConnected) return false;
    target.focus({ preventScroll: true });
    return document.activeElement === target;
  };
}
