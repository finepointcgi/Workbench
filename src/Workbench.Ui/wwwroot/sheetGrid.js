// Browser-side helpers for the sheet grid (Grid/SheetGrid.razor): default key handling,
// scrolling the active cell into view, focusing editors, column resizing, drag-selecting a range,
// the fill handle, and the clipboard (copy, cut and paste of tab-separated blocks, as Excel and Google Sheets use).

// Ctrl/Cmd shortcuts the grid handles itself (undo, redo, fill down, select all) instead of the browser's.
const commandKeys = new Set(['z', 'y', 'd', 'a']);

const navigationKeys = new Set([
    'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Enter', 'F2', 'Delete', 'Backspace',
    ' ', 'Home', 'End', 'PageUp', 'PageDown', 'Tab', 'Insert',
]);

export function init(container, dotNet) {
    // The grid handles these keys in .NET; stop the browser from scrolling, moving focus or typing.
    const onKeyDown = e => {
        if (e.target === container) {
            const printable = e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey;
            const command = (e.ctrlKey || e.metaKey) && !e.altKey && commandKeys.has(e.key.toLowerCase());
            if (navigationKeys.has(e.key) || printable || command) {
                e.preventDefault();
            }
        } else if (e.key === 'Tab' || (e.key === 'Enter' && e.target.closest('.wb-editing'))) {
            e.preventDefault();
        }
    };

    const onPointerDown = e => {
        if (e.button !== 0) {
            return;
        }

        if (e.target.closest('.wb-fill-handle')) {
            startFill(e);
            return;
        }

        const handle = e.target.closest('.wb-resize');
        if (!handle || !container.contains(handle)) {
            startRangeDrag(e);
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

    // Dragging across cells selects a range: the cell under the pointer becomes the active cell.
    const cellAt = (x, y) => {
        const td = document.elementFromPoint(x, y)?.closest('td.wb-cell');
        const tr = td?.closest('tr[data-row]');
        return td && tr && container.contains(td) ? { row: Number(tr.dataset.row), col: Number(td.dataset.col) } : null;
    };

    let drag = null;
    const startRangeDrag = e => {
        if (e.shiftKey || e.target.closest('input, select, textarea, button, a, .wb-toggle, .wb-check, .wb-editing, .mud-menu')) {
            return;
        }

        const start = cellAt(e.clientX, e.clientY);
        if (!start) {
            return;
        }

        drag = { start, last: start };
        const onMove = ev => {
            const cell = cellAt(ev.clientX, ev.clientY);
            if (!drag || !cell || (cell.row === drag.last.row && cell.col === drag.last.col)) {
                return;
            }

            drag.last = cell;
            container.classList.add('wb-dragging');
            dotNet.invokeMethodAsync('DragSelect', drag.start.row, drag.start.col, cell.row, cell.col);
        };
        const onUp = () => {
            window.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', onUp);
            container.classList.remove('wb-dragging');
            drag = null;
        };
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
    };

    // The fill handle (the square at the selection's corner): drag down, release, and the selection repeats down to there.
    const startFill = e => {
        e.preventDefault();
        e.stopPropagation();
        let target = null;
        const marked = [];
        const unmark = () => marked.splice(0).forEach(el => el.classList.remove('wb-fill-preview'));
        const onMove = ev => {
            const cell = cellAt(ev.clientX, ev.clientY);
            if (!cell || cell.row === target) {
                return;
            }

            target = cell.row;
            unmark();
            const corner = container.querySelector('.wb-fill-handle')?.closest('tr[data-row]');
            const from = corner ? Number(corner.dataset.row) : target;
            container.querySelectorAll('tr[data-row]').forEach(tr => {
                const r = Number(tr.dataset.row);
                if (r > from && r <= target) {
                    tr.classList.add('wb-fill-preview');
                    marked.push(tr);
                }
            });
        };
        const onUp = () => {
            window.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', onUp);
            unmark();
            if (target !== null) {
                dotNet.invokeMethodAsync('FillToAsync', target);
            }
        };
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
    };

    // The clipboard: only when the grid itself has focus (an open cell editor copies and pastes text as usual).
    const writeClipboard = async text => {
        try {
            await navigator.clipboard.writeText(text);
        } catch {
            const area = document.createElement('textarea');
            area.value = text;
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            document.execCommand('copy');
            area.remove();
            container.focus({ preventScroll: true });
        }
    };
    // Copy and cut run from the key press (every browser sends it) and from the copy/cut event (menus, other shortcuts);
    // whichever comes first does the work.
    let copiedAt = 0;
    const copy = cut => {
        copiedAt = Date.now();
        dotNet.invokeMethodAsync(cut ? 'CutSelectionAsync' : 'CopySelection').then(text => {
            if (text) {
                writeClipboard(text);
            }
        });
    };
    const onCopyKey = e => {
        if (e.target === container && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && ['c', 'x'].includes(e.key.toLowerCase())) {
            copy(e.key.toLowerCase() === 'x');
        }
    };
    const onCopy = e => {
        if (e.target !== container) {
            return;
        }

        e.preventDefault();
        if (Date.now() - copiedAt > 500) {
            copy(e.type === 'cut');
        }
    };
    const onPaste = e => {
        if (e.target !== container) {
            return;
        }

        const text = e.clipboardData?.getData('text/plain');
        e.preventDefault();
        if (text) {
            dotNet.invokeMethodAsync('PasteAsync', text);
        }
    };

    container.addEventListener('keydown', onKeyDown);
    container.addEventListener('keydown', onCopyKey);
    container.addEventListener('pointerdown', onPointerDown);
    container.addEventListener('copy', onCopy);
    container.addEventListener('cut', onCopy);
    container.addEventListener('paste', onPaste);
    return {
        dispose() {
            container.removeEventListener('keydown', onKeyDown);
            container.removeEventListener('keydown', onCopyKey);
            container.removeEventListener('pointerdown', onPointerDown);
            container.removeEventListener('copy', onCopy);
            container.removeEventListener('cut', onCopy);
            container.removeEventListener('paste', onPaste);
        },
    };
}

// Where to open the status list: just under the cell (viewport coordinates, for a position: fixed popover).
export function cellRect(container, rowIndex, colIndex) {
    const td = container?.querySelector(`tr[data-row="${rowIndex}"] td[data-col="${colIndex}"]`);
    if (!td) {
        return null;
    }

    const r = td.getBoundingClientRect();
    const left = Math.max(8, Math.min(r.left, window.innerWidth - 240));
    const top = r.bottom + 260 > window.innerHeight ? Math.max(8, r.top - 260) : r.bottom + 2;
    return [left, top];
}

export function focusIn(element, selector) {
    element?.querySelector(selector)?.focus({ preventScroll: true });
}

export function downloadBase64(fileName, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
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
