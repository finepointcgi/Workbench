// Installable app: registers the service worker (static app-shell files only, see sw.js) and shows a banner while the
// device is offline. Blazor Server needs a live connection, so nothing can be saved until it's back.
(() => {
  if ("serviceWorker" in navigator && window.isSecureContext) {
    window.addEventListener("load", () => {
      navigator.serviceWorker.register(new URL("sw.js", document.baseURI), { scope: new URL("./", document.baseURI).pathname })
        .catch(() => { /* Unsupported or blocked: the app still works online. */ });
    });
  }

  let banner;
  const update = () => {
    if (navigator.onLine) {
      banner?.remove();
      banner = undefined;
      return;
    }

    if (!banner) {
      banner = document.createElement("div");
      banner.className = "wb-offline-banner";
      banner.setAttribute("role", "status");
      banner.textContent = "You're offline. Workbench can't load or save anything until you reconnect.";
      document.body.appendChild(banner);
    }
  };

  window.addEventListener("online", update);
  window.addEventListener("offline", update);
  update();
})();
