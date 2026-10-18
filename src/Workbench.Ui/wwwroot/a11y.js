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
