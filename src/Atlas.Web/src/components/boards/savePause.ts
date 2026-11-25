/** A pause in typing before a board editor saves: `start` restarts it, `cancel` stops it. */
export function createSavePause(run: () => void, ms = 1000) {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return {
    start() { clearTimeout(timer); timer = setTimeout(run, ms); },
    cancel() { clearTimeout(timer); },
  };
}
