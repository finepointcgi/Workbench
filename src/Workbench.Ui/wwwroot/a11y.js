// Keyboard access to scrolling regions (WCAG 2.1.1; axe "scrollable-region-focusable"). A table that is wider than the
// screen (most of them at phone width) scrolls sideways, but a keyboard user can only scroll a region that can take
// focus. This makes such regions focusable while they overflow and have nothing focusable inside, and names them after
// their table. Plain script, no inline handlers, so the Content-Security-Policy stays strict.
(() => {
  const selector = ".mud-table-container, [data-wb-scroll]";
  const focusable = "a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";

  const nameFor = (region) => {
    const table = region.closest(".mud-table") || region;
    const labelled = region.dataset.wbScroll || table.getAttribute("aria-label")
      || table.querySelector("table")?.getAttribute("aria-label") || table.querySelector("caption")?.textContent;
    return (labelled || "Scrollable table").trim();
  };

  const update = () => {
    for (const region of document.querySelectorAll(selector)) {
      const overflows = region.scrollWidth > region.clientWidth || region.scrollHeight > region.clientHeight;
      const added = region.hasAttribute("data-wb-scroll-added");
      if (overflows && !added && !region.hasAttribute("tabindex") && !region.querySelector(focusable)) {
        region.setAttribute("tabindex", "0");
        region.setAttribute("role", "region");
        region.setAttribute("aria-label", nameFor(region));
        region.setAttribute("data-wb-scroll-added", "");
      } else if (added && (!overflows || region.querySelector(focusable))) {
        region.removeAttribute("tabindex");
        region.removeAttribute("role");
        region.removeAttribute("aria-label");
        region.removeAttribute("data-wb-scroll-added");
      }
    }
  };

  let pending = false;
  const schedule = () => {
    if (!pending) {
      pending = true;
      requestAnimationFrame(() => { pending = false; update(); });
    }
  };

  new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener("resize", schedule, { passive: true });
  schedule();
})();

// Focus stays on a button that disables itself while it works (WCAG 2.4.3). Most save and submit buttons render
// Disabled="_busy": a disabled button loses focus, the browser drops it on <body>, and a keyboard or screen reader user is
// sent back to the top of the page. This puts focus back on the button when it's enabled again. If it stays disabled (Save
// with nothing left to save), focus goes to the next control once the page has settled, so it never ends on <body>.
(() => {
  const focusable = "a[href], button:not([disabled]), input:not([disabled]):not([type='hidden']), select:not([disabled]), "
    + "textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";
  const settleMs = 1500;
  let focused = null;   // the control that last took focus
  let pending = null;   // a focused control that became disabled
  let lastChange = 0;   // when the page last changed

  const lost = () => !document.activeElement || document.activeElement === document.body;

  document.addEventListener("focusin", (event) => {
    focused = event.target;
    pending = null;
  }, true);

  new MutationObserver((records) => {
    lastChange = Date.now();
    for (const record of records) {
      const element = record.target;
      if (record.type !== "attributes") {
        continue;
      }

      if (element.disabled && element === focused) {
        pending = element;
      } else if (!element.disabled && element === pending) {
        pending = null;
        if (lost() && element.isConnected) {
          element.focus({ preventScroll: true });
        }
      }
    }
  }).observe(document.documentElement, { attributes: true, attributeFilter: ["disabled"], childList: true, subtree: true });

  const next = (from) => {
    const candidates = [...document.querySelectorAll(focusable)].filter((e) => e.offsetParent !== null || e === document.activeElement);
    return candidates.find((e) => from.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_FOLLOWING)
      ?? candidates.reverse().find((e) => from.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_PRECEDING);
  };

  setInterval(() => {
    if (!pending) {
      return;
    }

    if (!lost() || !pending.isConnected) {
      pending = null;
    } else if (Date.now() - lastChange >= settleMs) {
      const target = next(pending);
      pending = null;
      target?.focus({ preventScroll: true });
    }
  }, 250);
})();
