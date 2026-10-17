// Pointer handling for the Gantt timeline. Dragging is previewed locally with inline styles
// (which Blazor never renders), then reported to .NET once on release.
//   data-drag="move"   drag a bar to shift its dates
//   data-drag="resize" drag a bar's right edge to change its finish
//   data-drag="link"   drag from a bar's end handle onto another bar to add a dependency

const CLICK_SLOP = 3;

export function attach(svg, dotnet) {
    let drag = null;

    const point = e => {
        const r = svg.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top };
    };

    function down(e) {
        const handle = e.button === 0 && e.target.closest('[data-drag]');
        const bar = handle && handle.closest('[data-item]');
        if (!bar || !svg.contains(bar)) return;
        e.preventDefault();
        svg.setPointerCapture(e.pointerId);
        drag = { mode: handle.dataset.drag, bar, item: bar.dataset.item, startX: e.clientX, days: 0, pointerId: e.pointerId };
        if (drag.mode === 'link') {
            const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
            line.setAttribute('class', 'gantt-link-preview');
            const p = point(e);
            line.setAttribute('x1', p.x); line.setAttribute('y1', p.y);
            line.setAttribute('x2', p.x); line.setAttribute('y2', p.y);
            svg.querySelector('.gantt-overlay').appendChild(line);
            drag.line = line;
        } else {
            drag.rect = bar.querySelector('[data-role="bar"]');
            drag.width = drag.rect ? parseFloat(drag.rect.getAttribute('width')) : 0;
            bar.classList.add('dragging');
        }
    }

    function move(e) {
        if (!drag || e.pointerId !== drag.pointerId) return;
        if (drag.mode === 'link') {
            const p = point(e);
            drag.line.setAttribute('x2', p.x); drag.line.setAttribute('y2', p.y);
            svg.querySelectorAll('.link-target').forEach(el => el.classList.remove('link-target'));
            const target = targetAt(e, drag.item);
            if (target) target.classList.add('link-target');
            return;
        }
        const dayWidth = parseFloat(svg.dataset.dayWidth);
        const dx = e.clientX - drag.startX;
        let days = Math.abs(dx) < CLICK_SLOP ? 0 : Math.round(dx / dayWidth);
        if (drag.mode === 'resize') {
            // Keep at least one day (finish >= start).
            days = Math.max(days, Math.round(-drag.width / dayWidth) + 1);
            drag.rect.style.width = `${drag.width + days * dayWidth}px`;
            drag.bar.querySelectorAll('[data-end]').forEach(el => el.style.transform = `translate(${days * dayWidth}px, 0)`);
        } else {
            drag.bar.style.transform = `translate(${days * dayWidth}px, 0)`;
        }
        drag.days = days;
    }

    function targetAt(e, source) {
        const el = document.elementFromPoint(e.clientX, e.clientY);
        const target = el && el.closest('[data-link-target]');
        return target && svg.contains(target) && target.dataset.item !== source ? target : null;
    }

    async function up(e) {
        if (!drag || e.pointerId !== drag.pointerId) return;
        const d = drag;
        drag = null;
        svg.releasePointerCapture(e.pointerId);
        try {
            if (d.mode === 'link') {
                d.line.remove();
                const target = targetAt(e, d.item);
                svg.querySelectorAll('.link-target').forEach(el => el.classList.remove('link-target'));
                if (target) await dotnet.invokeMethodAsync('LinkDropped', d.item, target.dataset.item);
            } else if (d.days !== 0) {
                await dotnet.invokeMethodAsync('BarDropped', d.item, d.mode, d.days);
            }
        } finally {
            reset(d);
        }
    }

    function reset(d) {
        d.bar.classList.remove('dragging');
        d.bar.style.transform = '';
        if (d.rect) d.rect.style.width = '';
        d.bar.querySelectorAll('[data-end]').forEach(el => el.style.transform = '');
    }

    function cancel(e) {
        if (!drag || e.pointerId !== drag.pointerId) return;
        drag.line?.remove();
        reset(drag);
        drag = null;
        svg.querySelectorAll('.link-target').forEach(el => el.classList.remove('link-target'));
    }

    svg.addEventListener('pointerdown', down);
    svg.addEventListener('pointermove', move);
    svg.addEventListener('pointerup', up);
    svg.addEventListener('pointercancel', cancel);
    return {
        dispose() {
            svg.removeEventListener('pointerdown', down);
            svg.removeEventListener('pointermove', move);
            svg.removeEventListener('pointerup', up);
            svg.removeEventListener('pointercancel', cancel);
        },
    };
}

export function scrollToX(element, x) {
    if (element) element.scrollLeft = Math.max(0, x - element.clientWidth / 3);
}
