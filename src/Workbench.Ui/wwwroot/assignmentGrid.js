// The assignment grid: rows copied from Excel (tab or line separated) pasted anywhere in the grid go to .NET as text, so
// they become rows to check and add. A single value pasted into a cell stays a normal paste.
export function init(element, dotNet) {
    const onPaste = event => {
        const text = event.clipboardData ? event.clipboardData.getData('text/plain') : '';
        if (text && (text.includes('\t') || text.trim().includes('\n'))) {
            event.preventDefault();
            dotNet.invokeMethodAsync('OnPaste', text);
        }
    };
    element.addEventListener('paste', onPaste);
    return { dispose: () => element.removeEventListener('paste', onPaste) };
}

export function scrollIntoView(id, newRow) {
    const element = document.getElementById(id);
    if (element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'start' });
        // newRow: the empty last row, ready for a name; otherwise the first cell.
        const first = (newRow && element.querySelector('tr.wb-assign-new input')) || element.querySelector('input, select');
        if (first) {
            first.focus({ preventScroll: true });
        }
    }
}
