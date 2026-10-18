// Keeps the auth cookie's idle timeout in step with activity inside the Blazor circuit (which doesn't touch the
// cookie), and leaves for sign-in when the server says the session is over. No inline script, so the CSP stays strict.
(() => {
  const pingPath = "account/session/ping";
  const minIntervalMs = 4 * 60 * 1000;
  let lastPing = Date.now();
  let active = false;

  const markActive = () => { active = true; };
  for (const type of ["pointerdown", "keydown", "wheel", "touchstart"]) {
    window.addEventListener(type, markActive, { passive: true, capture: true });
  }

  setInterval(async () => {
    if (!active || Date.now() - lastPing < minIntervalMs) {
      return;
    }

    active = false;
    lastPing = Date.now();
    try {
      const response = await fetch(document.baseURI + pingPath, { credentials: "same-origin", redirect: "manual", cache: "no-store" });
      if (response.status === 401 || response.type === "opaqueredirect") {
        location.assign(document.baseURI + "account/signin?returnUrl=" + encodeURIComponent(location.pathname + location.search));
      }
    } catch {
      // Offline: Blazor's reconnect UI handles it.
    }
  }, 30 * 1000);
})();
