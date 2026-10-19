// The assistant panel's helpers: copying an answer and keeping the newest message in view.
// No inline handlers (CSP); everything is called from Blazor through JS interop.

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        // Clipboard blocked (permissions, insecure context): fall back to a hidden textarea.
        try {
            const area = document.createElement('textarea');
            area.value = text;
            area.setAttribute('readonly', '');
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            const ok = document.execCommand('copy');
            area.remove();
            return ok;
        } catch {
            return false;
        }
    }
}

export function scrollToEnd(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}
