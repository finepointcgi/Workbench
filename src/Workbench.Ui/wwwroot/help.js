// Welcome tour helpers: point at an element and move focus. Loaded as a module (no inline script; CSP-safe).
// Highlighting uses inline styles on the target, restored afterwards, so no global stylesheet is needed.

let current = null;

export function highlight(selector) {
    clear();
    if (!selector) {
        return false;
    }

    let element = null;
    try {
        element = document.querySelector(selector);
    } catch {
        return false;
    }

    if (!element || element.getClientRects().length === 0) {
        return false;
    }

    current = {
        element,
        boxShadow: element.style.boxShadow,
        borderRadius: element.style.borderRadius,
        transition: element.style.transition,
    };

    // Two inset rings (dark outside, light inside) stay visible on the blue app bar, the white menu and dark mode,
    // and aren't clipped by the menu's scroll area.
    element.style.boxShadow = 'inset 0 0 0 3px #0b0c0c, inset 0 0 0 6px #ffdd00';
    element.style.borderRadius = '6px';
    element.style.transition = 'box-shadow 120ms ease-in';
    element.setAttribute('data-wb-tour-target', 'true');
    try {
        element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    } catch {
        // Old browsers: not scrolled; the highlight is still there.
    }

    return true;
}

export function clear() {
    if (!current) {
        return;
    }

    const { element, boxShadow, borderRadius, transition } = current;
    element.style.boxShadow = boxShadow;
    element.style.borderRadius = borderRadius;
    element.style.transition = transition;
    element.removeAttribute('data-wb-tour-target');
    current = null;
}

export function focus(id) {
    const element = document.getElementById(id);
    if (element) {
        element.focus({ preventScroll: true });
    }
}
