// Navigation helpers: the Ctrl+K / Cmd+K and N shortcuts, recently visited pages and the phone (/m) preference.
// Storage can be missing or blocked (private windows, cleared site data), so every read and write is guarded.

const recentKey = 'workbench.nav.recent';
const siteKey = 'workbench.nav.site';
const newKeyKey = 'workbench.nav.newkey';
const maxRecent = 8;
let shortcutHandler = null;

function read(key) {
    try {
        return window.localStorage.getItem(key);
    } catch {
        return null;
    }
}

function write(key, value) {
    try {
        if (value === null) {
            window.localStorage.removeItem(key);
        } else {
            window.localStorage.setItem(key, value);
        }
    } catch {
        // Not remembered on this device; nothing else depends on it.
    }
}

export function isMac() {
    return /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent || '');
}

export function registerShortcut(dotnet) {
    unregisterShortcut();
    shortcutHandler = e => {
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
            e.preventDefault();
            dotnet.invokeMethodAsync('OpenFromShortcut');
        }
    };
    document.addEventListener('keydown', shortcutHandler);
}

export function unregisterShortcut() {
    if (shortcutHandler) {
        document.removeEventListener('keydown', shortcutHandler);
        shortcutHandler = null;
    }
}

// N opens the "+ New" menu, unless the user is typing, a modifier is held, a dialog or menu is already open, or the
// user turned the shortcut off on this device (account menu; WCAG 2.1.4 asks that single-key shortcuts can be).
let newShortcutHandler = null;

function isTyping(target) {
    const el = target instanceof Element ? target : null;
    if (!el) {
        return false;
    }
    return el.isContentEditable || !!el.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"], [role="textbox"], [role="combobox"], [role="grid"], [role="treegrid"], [role="application"]');
}

export function registerNewShortcut(dotnet) {
    unregisterNewShortcut();
    newShortcutHandler = e => {
        if ((e.key !== 'n' && e.key !== 'N') || e.ctrlKey || e.metaKey || e.altKey || e.repeat || e.defaultPrevented || !getNewShortcut()) {
            return;
        }
        if (isTyping(e.target) || isTyping(document.activeElement) || document.querySelector('.mud-dialog, .mud-popover-open .mud-menu-list')) {
            return;
        }
        e.preventDefault();
        dotnet.invokeMethodAsync('OpenFromShortcut');
    };
    document.addEventListener('keydown', newShortcutHandler);
}

export function getNewShortcut() {
    return read(newKeyKey) !== 'off';
}

export function setNewShortcut(on) {
    write(newKeyKey, on ? null : 'off');
}

export function unregisterNewShortcut() {
    if (newShortcutHandler) {
        document.removeEventListener('keydown', newShortcutHandler);
        newShortcutHandler = null;
    }
}

export function getRecent() {
    try {
        const items = JSON.parse(read(recentKey) || '[]');
        return Array.isArray(items) ? items.filter(i => i && typeof i.href === 'string' && typeof i.title === 'string') : [];
    } catch {
        return [];
    }
}

// Remembers a visited page under its title. The page sets its <title> once its data loads, so read it a moment later.
export function recordVisit(href) {
    window.setTimeout(() => {
        const current = (location.pathname + location.search).replace(/^\//, '');
        if (current !== href) {
            return;
        }
        const title = (document.title || '').replace(/\s*·\s*Workbench\s*$/, '').trim();
        if (!title || title === 'Workbench') {
            return;
        }
        const items = getRecent().filter(i => i.href !== href);
        items.unshift({ href, title });
        write(recentKey, JSON.stringify(items.slice(0, maxRecent)));
    }, 1500);
}

// "full", "mobile" or null (not chosen on this device).
export function getSitePreference() {
    const value = read(siteKey);
    return value === 'full' || value === 'mobile' ? value : null;
}

export function setSitePreference(value) {
    write(siteKey, value === 'full' || value === 'mobile' ? value : null);
}

export function isSmallScreen() {
    try {
        return window.matchMedia('(max-width: 600px)').matches;
    } catch {
        return false;
    }
}
