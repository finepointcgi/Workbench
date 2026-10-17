// Browser-side helpers for the sheet grid (Grid/SheetGrid.razor): default key handling,
// scrolling the active cell into view, focusing editors and column resizing.

const navigationKeys = new Set([
    'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Enter', 'F2', 'Delete', 'Backspace',
    ' ', 'Home', 'End', 'PageUp', 'PageDown', 'Tab',
]);

export function init(container, dotNet) {
    // The grid handles these keys in .NET; stop the browser from scrolling, moving focus or typing.
    const onKeyDown = e => {
        if (e.target === container) {
            const printable = e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey;
            if (navigationKeys.has(e.key) || printable) {
                e.preventDefault();
            }
        } else if (e.key === 'Tab' || (e.key === 'Enter' && e.target.closest('.wb-editing'))) {
            e.preventDefault();
        }
    };

    const onPointerDown = e => {
        const handle = e.target.closest('.wb-resize');
        if (!handle || !container.contains(handle)) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();
        const key = handle.dataset.key;
        const col = container.querySelector(`col[data-key="${CSS.escape(key)}"]`);
        const table = container.querySelector('table');
        if (!col || !table) {
            return;
        }

        const startX = e.clientX;
        const startWidth = parseFloat(col.style.width) || col.getBoundingClientRect().width;
        const startTableWidth = parseFloat(table.style.width) || table.offsetWidth;
        let width = startWidth;
        handle.setPointerCapture(e.pointerId);

        const onMove = ev => {
            width = Math.max(60, Math.round(startWidth + ev.clientX - startX));
            col.style.width = `${width}px`;
            table.style.width = `${startTableWidth + width - startWidth}px`;
        };
        const onUp = () => {
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onUp);
            handle.removeEventListener('pointercancel', onUp);
            if (width !== startWidth) {
                dotNet.invokeMethodAsync('SetColumnWidth', key, width);
            }
        };
        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onUp);
        handle.addEventListener('pointercancel', onUp);
    };

    container.addEventListener('keydown', onKeyDown);
    container.addEventListener('pointerdown', onPointerDown);
    return {
        dispose() {
            container.removeEventListener('keydown', onKeyDown);
            container.removeEventListener('pointerdown', onPointerDown);
        },
    };
}

// Scrolls so the cell at (rowIndex, colIndex) is visible below the sticky header and right of the sticky columns.
// Rows are virtualized, so the row may not be in the DOM yet: compute its position from the row height.
export function ensureVisible(container, rowIndex, colIndex) {
    const head = container.querySelector('thead');
    const headHeight = head ? head.offsetHeight : 0;
    const sample = container.querySelector('tr.wb-row');
    const rowHeight = sample ? sample.offsetHeight : 34;

    const top = rowIndex * rowHeight;
    const viewHeight = container.clientHeight - headHeight;
    if (top < container.scrollTop) {
        container.scrollTop = top;
    } else if (top + rowHeight > container.scrollTop + viewHeight) {
        container.scrollTop = top + rowHeight - viewHeight;
    }

    const th = container.querySelector(`thead th[data-col="${colIndex}"]`);
    if (!th || th.classList.contains('wb-sticky')) {
        return;
    }

    const stickyWidth = [...container.querySelectorAll('thead th.wb-sticky')].reduce((w, el) => w + el.offsetWidth, 0);
    if (th.offsetLeft - stickyWidth < container.scrollLeft) {
        container.scrollLeft = th.offsetLeft - stickyWidth;
    } else if (th.offsetLeft + th.offsetWidth > container.scrollLeft + container.clientWidth) {
        container.scrollLeft = th.offsetLeft + th.offsetWidth - container.clientWidth;
    }
}

export function focusEditor(element, selectAll) {
    if (!element) {
        return;
    }

    element.focus({ preventScroll: true });
    if (typeof element.setSelectionRange === 'function' && typeof element.value === 'string') {
        try {
            if (selectAll) {
                element.select();
            } else {
                element.setSelectionRange(element.value.length, element.value.length);
            }
        } catch {
            // Some input types (email, number) don't support selection ranges.
        }
    }
}

export function focusGrid(container) {
    container?.focus({ preventScroll: true });
}
