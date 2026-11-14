// The first view's reads, started while the app's code downloads (load-speed review 2026-10-28, round 2). A classic async
// script of its own: the policy allows no inline script, and inside the app bundle it would wait for the whole bundle.
// src/api.ts adopts each response once, for the same GET the app makes anyway, and App.vue drops what it did not use.
// When the app is already running (its code came from the cache first), it has made its own requests: do nothing.
(function () {
  if (window.atlasEarlyReads || (location.pathname !== "/" && location.pathname !== "/index.html")) return;
  var reads = (window.atlasEarlyReads = new Map());
  function read(path) {
    var request = fetch("/api" + path, { credentials: "same-origin" });
    request.catch(function () {});
    reads.set(path, request);
    return request;
  }
  read("/setup/status");
  // As App.vue reads them after a known session: the spaces and the modules. (Not an administrator's module setup: the app
  // asks for it as soon as it has read the account, before this script could.)
  read("/auth/me").then(function (response) {
    if (!response.ok) return;
    read("/spaces");
    read("/modules");
  }).catch(function () {});
  // As App.vue prefetches them from the address: the page opened (or its access settings), the tree of the space opened.
  var id = "([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})";
  var page = new RegExp("^#/pages/" + id + "(?:$|\\?|/access(?:/|$))").exec(location.hash);
  // With its bundle and its live state beside it, as App.vue's prefetchPage asks (readPageBundle): a plain read was never adopted and the
  // page was read twice.
  if (page) {
    read("/pages/" + page[1] + "/collaboration/state");
    read("/pages/" + page[1] + "?bundle=true");
  }
  var space = new RegExp("^#/spaces/" + id + "(?:$|\\?)").exec(location.hash);
  if (space) read("/spaces/" + space[1] + "/tree");
})();
