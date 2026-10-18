// Keyboard matching on the reconciliation screen: keep arrow keys and space from scrolling the page while a pane
// has focus (Blazor handles the keys themselves). Kept in a file for the Content-Security-Policy.
const keys = new Set(["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", " ", "Home", "End"]);

export function trapKeys(element) {
    if (!element || element.dataset.cashKeys) {
        return;
    }

    element.dataset.cashKeys = "1";
    element.addEventListener("keydown", e => {
        if (keys.has(e.key) && !(e.target instanceof HTMLInputElement) && !(e.target instanceof HTMLTextAreaElement)) {
            e.preventDefault();
        }
    });
}

export function focus(element) {
    element?.focus();
}

export function scrollIntoView(id) {
    document.getElementById(id)?.scrollIntoView({ block: "nearest" });
}
