// Keeps the auth cookie's idle timeout in step with activity inside the Blazor circuit (which doesn't touch the
// cookie), leaves for sign-in when the server says the session is over, and warns before the idle sign-out (WCAG 2.2.1).
// No inline script, so the CSP stays strict.
(() => {
  const pingPath = "account/session/ping";
  const minIntervalMs = 4 * 60 * 1000;
  let lastPing = Date.now();
  let active = false;

  const ping = async () => {
    lastPing = Date.now();
    try {
      const response = await fetch(document.baseURI + pingPath, { credentials: "same-origin", redirect: "manual", cache: "no-store" });
      if (response.status === 401 || response.type === "opaqueredirect") {
        location.assign(document.baseURI + "account/signin?returnUrl=" + encodeURIComponent(location.pathname + location.search));
      }
    } catch {
      // Offline: Blazor's reconnect UI handles it.
    }
  };

  const markActive = () => { active = true; };
  for (const type of ["pointerdown", "keydown", "wheel", "touchstart"]) {
    window.addEventListener(type, markActive, { passive: true, capture: true });
  }

  setInterval(() => {
    if (!active || Date.now() - lastPing < minIntervalMs) {
      return;
    }

    active = false;
    ping();
  }, 30 * 1000);

  // Idle warning. App.razor emits the settings for signed-in users only. The warning is plain DOM, not a Blazor dialog:
  // anything the circuit renders counts as activity on the server and would reset the idle clock it warns about.
  const config = document.querySelector('meta[name="wb-session"]');
  const idleMs = Number(config?.dataset.idleSeconds) * 1000;
  if (!idleMs) {
    return;
  }

  const endsAt = Number(config.dataset.endsAt) || 0;
  const warnMs = Math.min(2 * 60 * 1000, idleMs / 2);
  const storageKey = "wb-last-activity";
  const keepAliveEveryMs = 60 * 1000;
  let lastInput = Date.now();
  let lastShared = 0;
  let lastKeepAlive = Date.now();
  let dialog = null;
  let mode = null; // "idle" or "ending" while the dialog is open
  let endingAcknowledged = false;

  // Activity is shared between tabs, so working in one tab doesn't sign out another.
  const readShared = () => {
    try {
      return Number(localStorage.getItem(storageKey)) || 0;
    } catch {
      return 0;
    }
  };

  const noteInput = (event) => {
    if (dialog?.open && dialog.contains(event.target)) {
      return; // Tabbing around the warning isn't an answer to it.
    }

    lastInput = Date.now();
    if (lastInput - lastShared > 5000) {
      lastShared = lastInput;
      try {
        localStorage.setItem(storageKey, String(lastInput));
      } catch {
        // Storage blocked: each tab tracks its own activity.
      }
    }
  };
  for (const type of ["pointerdown", "keydown", "wheel", "touchstart"]) {
    window.addEventListener(type, noteInput, { passive: true, capture: true });
  }

  const lastActivity = () => Math.max(lastInput, readShared());

  // Resets the circuit's idle clock: any call into .NET counts as activity (SessionGuard.KeepSessionAlive).
  const keepCircuitAlive = () => {
    lastKeepAlive = Date.now();
    try {
      window.DotNet?.invokeMethodAsync("Workbench.Web", "KeepSessionAlive").catch(() => {});
    } catch {
      // No circuit yet (or it's gone): the reconnect UI or SessionGuard handles it.
    }
  };

  const signOut = () => {
    location.assign(document.baseURI + "account/signout?returnUrl=" + encodeURIComponent(location.pathname + location.search));
  };

  const button = (action, text, primary) => {
    const element = document.createElement("button");
    element.type = "button";
    element.dataset.action = action;
    element.className = "mud-button-root mud-button mud-button-size-medium mud-ripple "
      + (primary ? "mud-button-filled mud-button-filled-primary mud-button-filled-size-medium" : "mud-button-text mud-button-text-primary mud-button-text-size-medium");
    const label = document.createElement("span");
    label.className = "mud-button-label";
    label.textContent = text;
    element.append(label);
    return element;
  };

  const build = () => {
    const element = document.createElement("dialog");
    element.className = "wb-session-dialog";
    element.setAttribute("aria-labelledby", "wb-session-title");
    element.setAttribute("aria-describedby", "wb-session-text");
    const title = document.createElement("h2");
    title.id = "wb-session-title";
    title.className = "mud-typography mud-typography-h6";
    const text = document.createElement("p");
    text.id = "wb-session-text";
    text.className = "mud-typography mud-typography-body1";
    const actions = document.createElement("div");
    actions.className = "wb-session-actions";
    element.append(title, text, actions);
    element.addEventListener("click", (event) => {
      const action = event.target.closest("button")?.dataset.action;
      if (action === "stay") {
        stay();
      } else if (action === "signout") {
        signOut();
      } else if (action === "ok") {
        endingAcknowledged = true;
        close();
      }
    });
    // Escape answers the warning too: whoever pressed it is here.
    element.addEventListener("cancel", (event) => {
      event.preventDefault();
      if (mode === "idle") {
        stay();
      } else {
        endingAcknowledged = true;
        close();
      }
    });
    document.body.append(element);
    return element;
  };

  const countdown = (ms) => {
    const seconds = Math.max(0, Math.ceil(ms / 1000));
    const minutes = Math.floor(seconds / 60);
    const rest = seconds % 60;
    return minutes > 0 ? `${minutes}:${String(rest).padStart(2, "0")} minutes` : `${rest} seconds`;
  };

  const open = (newMode, remainingMs) => {
    dialog ??= build();
    mode = newMode;
    const title = dialog.querySelector("h2");
    const actions = dialog.querySelector(".wb-session-actions");
    actions.replaceChildren();
    let focus;
    if (newMode === "idle") {
      title.textContent = "Are you still there?";
      const stayButton = button("stay", "Stay signed in", true);
      actions.append(button("signout", "Sign out", false), stayButton);
      focus = stayButton;
    } else {
      title.textContent = "Your session is ending";
      focus = button("ok", "OK", true);
      actions.append(focus);
    }

    update(remainingMs);
    if (!dialog.open) {
      dialog.showModal();
    }

    focus.focus();
  };

  const update = (remainingMs) => {
    const text = dialog.querySelector("p");
    const left = countdown(remainingMs);
    text.textContent = mode === "idle"
      ? `You haven't used Workbench for a while. For security, you'll be signed out in ${left}, and changes you haven't saved will be lost.`
      : `For security, sessions end a set time after signing in. You'll be signed out in ${left}. Save your work now, then sign in again.`;
  };

  const close = () => {
    mode = null;
    if (dialog?.open) {
      dialog.close();
    }
  };

  const stay = () => {
    lastInput = Date.now();
    lastShared = 0;
    noteInput({ target: document.body });
    close();
    keepCircuitAlive();
    ping();
  };

  setInterval(() => {
    const now = Date.now();
    const activity = lastActivity();

    // Activity in another tab keeps this tab's circuit alive too (its own clicks already do).
    if (activity > lastKeepAlive && now - lastKeepAlive >= keepAliveEveryMs) {
      keepCircuitAlive();
    }

    if (endsAt && now >= endsAt) {
      signOut();
      return;
    }

    const idleDeadline = activity + idleMs;
    if (now >= idleDeadline) {
      signOut();
      return;
    }

    if (now >= idleDeadline - warnMs) {
      if (mode === "idle") {
        update(idleDeadline - now);
      } else {
        open("idle", idleDeadline - now);
      }
    } else if (endsAt && !endingAcknowledged && now >= endsAt - warnMs) {
      if (mode === "ending") {
        update(endsAt - now);
      } else {
        open("ending", endsAt - now);
      }
    } else if (mode === "idle") {
      close(); // Active again in another tab.
    }
  }, 1000);
})();
