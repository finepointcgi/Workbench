// The org chart viewport: drag to pan, Ctrl/Cmd + wheel or pinch to zoom, arrow keys to pan, + and - to zoom, 0 to fit.
// The tree is plain HTML inside `canvas`; this only moves and scales it (transform), so Blazor can re-render the tree
// (expand/collapse) without disturbing the view. A plain wheel still scrolls the page.
export function attach(viewport, canvas) {
    let x = 0, y = 0, scale = 1;
    const MIN = 0.2, MAX = 2;
    const apply = () => { canvas.style.transform = `translate(${x}px, ${y}px) scale(${scale})`; };

    const zoomAt = (factor, cx, cy) => {
        const next = Math.min(MAX, Math.max(MIN, scale * factor));
        const k = next / scale;
        x = cx - (cx - x) * k;
        y = cy - (cy - y) * k;
        scale = next;
        apply();
    };
    const center = () => { const r = viewport.getBoundingClientRect(); return [r.width / 2, r.height / 2]; };

    // Fit shows everyone; the first view stays readable instead (at least 60%) and starts centred, so you pan from there.
    const fit = (readable) => {
        const r = viewport.getBoundingClientRect();
        const w = canvas.scrollWidth, h = canvas.scrollHeight;
        if (!w || !h) { return; }
        scale = Math.min(1, Math.max(readable === true ? 0.6 : MIN, Math.min((r.width - 32) / w, (r.height - 32) / h)));
        x = (r.width - w * scale) / 2;
        y = 16;
        apply();
    };

    // Pointer drag (mouse, pen, one finger) and pinch (two fingers).
    const pointers = new Map();
    let dragged = false, pinch = 0;
    const onDown = e => {
        if (e.button !== undefined && e.button !== 0) { return; }
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        dragged = false;
        pinch = pointers.size === 2 ? distance() : 0;
    };
    const distance = () => { const [a, b] = [...pointers.values()]; return Math.hypot(a.x - b.x, a.y - b.y); };
    const onMove = e => {
        const p = pointers.get(e.pointerId);
        if (!p) { return; }
        const dx = e.clientX - p.x, dy = e.clientY - p.y;
        if (pointers.size === 2) {
            const was = pinch;
            p.x = e.clientX; p.y = e.clientY;
            pinch = distance();
            if (was > 0) {
                const [a, b] = [...pointers.values()], r = viewport.getBoundingClientRect();
                zoomAt(pinch / was, (a.x + b.x) / 2 - r.left, (a.y + b.y) / 2 - r.top);
            }
            dragged = true;
            return;
        }
        if (!dragged && Math.hypot(dx, dy) < 4) { return; }
        if (!dragged) { dragged = true; viewport.setPointerCapture(e.pointerId); viewport.classList.add('wb-org-panning'); }
        p.x = e.clientX; p.y = e.clientY;
        x += dx; y += dy;
        apply();
    };
    const onUp = e => {
        pointers.delete(e.pointerId);
        pinch = 0;
        if (pointers.size === 0) { viewport.classList.remove('wb-org-panning'); }
    };
    // A drag that ends over a link or button must not also click it.
    const onClick = e => { if (dragged) { e.preventDefault(); e.stopPropagation(); dragged = false; } };
    const onWheel = e => {
        if (!(e.ctrlKey || e.metaKey)) { return; }
        e.preventDefault();
        const r = viewport.getBoundingClientRect();
        zoomAt(Math.exp(-e.deltaY * 0.01), e.clientX - r.left, e.clientY - r.top);
    };
    const onKey = e => {
        if (e.target !== viewport) { return; }
        const step = 60;
        const moves = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] };
        if (moves[e.key]) { x += moves[e.key][0]; y += moves[e.key][1]; apply(); }
        else if (e.key === '+' || e.key === '=') { zoomAt(1.2, ...center()); }
        else if (e.key === '-') { zoomAt(1 / 1.2, ...center()); }
        else if (e.key === '0') { fit(false); }
        else { return; }
        e.preventDefault();
    };

    viewport.addEventListener('pointerdown', onDown);
    viewport.addEventListener('pointermove', onMove);
    viewport.addEventListener('pointerup', onUp);
    viewport.addEventListener('pointercancel', onUp);
    viewport.addEventListener('click', onClick, true);
    viewport.addEventListener('wheel', onWheel, { passive: false });
    viewport.addEventListener('keydown', onKey);
    fit(true);

    return {
        zoomIn: () => zoomAt(1.2, ...center()),
        zoomOut: () => zoomAt(1 / 1.2, ...center()),
        fit: () => fit(false),
        dispose: () => {
            viewport.removeEventListener('pointerdown', onDown);
            viewport.removeEventListener('pointermove', onMove);
            viewport.removeEventListener('pointerup', onUp);
            viewport.removeEventListener('pointercancel', onUp);
            viewport.removeEventListener('click', onClick, true);
            viewport.removeEventListener('wheel', onWheel);
            viewport.removeEventListener('keydown', onKey);
        },
    };
}
