// ── Mermaid zoom + pan + download helpers ───────────────────────────
// Depends on: isDarkMode (from app.js), mermaidCache (from markdown-renderer.js)

const MERMAID_ZOOM_MIN = 0.3;
const MERMAID_ZOOM_MAX = 6;
const MERMAID_ZOOM_STEP = 0.4;

/**
 * Wrap a rendered mermaid diagram in a zoom container with toolbar buttons.
 * Called after mermaid.render injects the SVG.
 */
function wrapMermaidWithZoom(diagramEl, svgHtml) {
    const wrapper = document.createElement('div');
    wrapper.className = 'mermaid-zoom-wrapper';

    // Toolbar
    const toolbar = document.createElement('div');
    toolbar.className = 'mermaid-zoom-toolbar';

    const btnZoomIn  = createZoomBtn('fas fa-search-plus',  'Zoom in',       () => adjustZoom(wrapper,  MERMAID_ZOOM_STEP));
    const btnZoomOut = createZoomBtn('fas fa-search-minus', 'Zoom out',      () => adjustZoom(wrapper, -MERMAID_ZOOM_STEP));
    const btnFit     = createZoomBtn('fas fa-expand',       'Fit / Reset',   () => resetZoom(wrapper));
    const btnDownload= createZoomBtn('fas fa-download',     'Save as image', () => showDownloadMenu(wrapper, content));

    toolbar.appendChild(btnZoomIn);
    toolbar.appendChild(btnZoomOut);
    toolbar.appendChild(btnFit);
    toolbar.appendChild(btnDownload);

    // Viewport (scrollable area)
    const viewport = document.createElement('div');
    viewport.className = 'mermaid-zoom-viewport';

    // Content (holds the SVG, gets transformed)
    const content = document.createElement('div');
    content.className = 'mermaid-zoom-content';
    content.innerHTML = svgHtml;
    content.dataset.zoom = '1';

    viewport.appendChild(content);
    wrapper.appendChild(toolbar);
    wrapper.appendChild(viewport);

    // ── Pan (drag-to-scroll) ────────────────────────────────────────
    let isDragging = false;
    let dragStartX = 0;
    let dragStartY = 0;
    let scrollStartX = 0;
    let dragStartScrollTop = 0;
    let dragStartScrollLeft = 0;
    let dragMoved = false;

    viewport.addEventListener('mousedown', (e) => {
        // Don't start drag if clicking on the toolbar or its children
        if (e.target.closest('.mermaid-zoom-toolbar')) return;

        isDragging = true;
        dragMoved = false;
        dragStartX = e.clientX;
        dragStartY = e.clientY;
        dragStartScrollLeft = viewport.scrollLeft;
        dragStartScrollTop = viewport.scrollTop;
        viewport.classList.add('is-dragging');
        e.preventDefault();
    });

    document.addEventListener('mousemove', (e) => {
        if (!isDragging) return;
        const dx = e.clientX - dragStartX;
        const dy = e.clientY - dragStartY;
        if (Math.abs(dx) > 3 || Math.abs(dy) > 3) dragMoved = true;
        viewport.scrollLeft = dragStartScrollLeft - dx;
        viewport.scrollTop  = dragStartScrollTop  - dy;
    });

    document.addEventListener('mouseup', () => {
        if (isDragging) {
            isDragging = false;
            viewport.classList.remove('is-dragging');
        }
    });

    // ── Wheel zoom (ctrl+wheel) ─────────────────────────────────────
    viewport.addEventListener('wheel', (e) => {
        if (e.ctrlKey) {
            e.preventDefault();
            const delta = e.deltaY < 0 ? MERMAID_ZOOM_STEP : -MERMAID_ZOOM_STEP;
            adjustZoom(wrapper, delta);
        }
    }, { passive: false });

    // Replace the original .mermaid div with the wrapper
    diagramEl.replaceWith(wrapper);
}

// ── Zoom helpers ────────────────────────────────────────────────────

function createZoomBtn(iconClass, title, onClick) {
    const btn = document.createElement('button');
    btn.className = 'mermaid-zoom-btn';
    btn.title = title;
    btn.innerHTML = `<i class="${iconClass}"></i>`;
    btn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        onClick();
    });
    return btn;
}

function adjustZoom(wrapper, delta) {
    const content = wrapper.querySelector('.mermaid-zoom-content');
    if (!content) return;

    const svg = content.querySelector('svg');
    const viewport = wrapper.querySelector('.mermaid-zoom-viewport');
    const prevZoom = parseFloat(content.dataset.zoom || '1');

    // Cache base SVG height BEFORE applying new transform.
    // getBoundingClientRect includes transform, so divide by prevZoom to get layout height.
    if (svg && !content.dataset.baseH) {
        const rectH = svg.getBoundingClientRect().height;
        const baseH = rectH / prevZoom;
        if (baseH > 0) content.dataset.baseH = baseH.toFixed(0);
    }

    let zoom = prevZoom + delta;
    zoom = Math.max(MERMAID_ZOOM_MIN, Math.min(MERMAID_ZOOM_MAX, zoom));
    content.dataset.zoom = zoom.toFixed(2);
    content.style.transform = `scale(${zoom})`;

    if (zoom > 1.01) {
        // Set viewport HEIGHT (not content) for vertical scroll.
        // Content stays responsive; viewport grows to fit scaled SVG + padding.
        const baseH = parseFloat(content.dataset.baseH || '0');
        if (baseH > 0 && viewport) {
            // 16px = top+bottom padding (8px each) on viewport
            viewport.style.height = (baseH * zoom + 16).toFixed(0) + 'px';
        }
        if (viewport) viewport.classList.add('is-zoomed');
    } else {
        if (viewport) {
            viewport.style.height = '';
            viewport.classList.remove('is-zoomed');
        }
    }
}

function resetZoom(wrapper) {
    const content = wrapper.querySelector('.mermaid-zoom-content');
    if (!content) return;
    content.dataset.zoom = '1';
    content.dataset.baseH = '';
    content.style.transform = 'scale(1)';
    const viewport = wrapper.querySelector('.mermaid-zoom-viewport');
    if (viewport) {
        viewport.style.height = '';
        viewport.classList.remove('is-zoomed');
        viewport.scrollTop = 0;
        viewport.scrollLeft = 0;
    }
}

// ── Download helpers ────────────────────────────────────────────────

/**
 * Show a small popup menu with SVG and PNG download options.
 * Menu is appended to document.body with position:fixed to escape
 * the wrapper's overflow:hidden.
 */
function showDownloadMenu(wrapper, content) {
    // Remove any existing menu
    const existing = document.querySelector('.mermaid-download-menu');
    if (existing) {
        existing.remove();
        return;
    }

    const svgEl = content.querySelector('svg');
    if (!svgEl) return;

    const menu = document.createElement('div');
    menu.className = 'mermaid-download-menu';

    const btnSvg = document.createElement('button');
    btnSvg.className = 'mermaid-download-item';
    btnSvg.innerHTML = '<i class="fas fa-file-code"></i> SVG';
    btnSvg.addEventListener('click', (e) => {
        e.preventDefault();
        menu.remove();
        downloadMermaidSvg(svgEl);
    });

    const btnPng = document.createElement('button');
    btnPng.className = 'mermaid-download-item';
    btnPng.innerHTML = '<i class="fas fa-file-image"></i> PNG (2x)';
    btnPng.addEventListener('click', (e) => {
        e.preventDefault();
        menu.remove();
        downloadMermaidPng(svgEl, 2);
    });

    const btnPngHd = document.createElement('button');
    btnPngHd.className = 'mermaid-download-item';
    btnPngHd.innerHTML = '<i class="fas fa-image"></i> PNG (4x HD)';
    btnPngHd.addEventListener('click', (e) => {
        e.preventDefault();
        menu.remove();
        downloadMermaidPng(svgEl, 4);
    });

    menu.appendChild(btnSvg);
    menu.appendChild(btnPng);
    menu.appendChild(btnPngHd);

    // Position menu below the download button using fixed coordinates
    const btn = wrapper.querySelector('.mermaid-zoom-btn[title="Save as image"]');
    if (btn) {
        const rect = btn.getBoundingClientRect();
        menu.style.top = (rect.bottom + 4) + 'px';
        menu.style.right = (window.innerWidth - rect.right) + 'px';
    } else {
        menu.style.top = '40px';
        menu.style.right = '10px';
    }

    document.body.appendChild(menu);

    // Close on outside click
    setTimeout(() => {
        const closeHandler = (ev) => {
            if (!menu.contains(ev.target) && !ev.target.closest('.mermaid-zoom-btn[title="Save as image"]')) {
                menu.remove();
                document.removeEventListener('mousedown', closeHandler);
            }
        };
        document.addEventListener('mousedown', closeHandler);
    }, 0);
}

/**
 * Get a clean clone of the SVG with explicit width/height.
 */
function cloneSvgForExport(svgEl) {
    const clone = svgEl.cloneNode(true);
    // Remove mermaid's max-width style that limits display size
    clone.style.maxWidth = null;
    clone.style.maxWidth = '';
    clone.style.transform = null;
    clone.style.transform = '';

    // Use viewBox for natural dimensions (transform-independent)
    let width = 0, height = 0;
    const vb = svgEl.viewBox?.baseVal;
    if (vb && vb.width > 0) {
        width = vb.width;
        height = vb.height;
    } else {
        // Fallback: offsetWidth/Height (layout size, not affected by transform)
        width = svgEl.offsetWidth || parseFloat(svgEl.getAttribute('width')) || 800;
        height = svgEl.offsetHeight || parseFloat(svgEl.getAttribute('height')) || 600;
    }

    clone.setAttribute('width', width);
    clone.setAttribute('height', height);
    return { clone, width, height };
}

/**
 * Download the diagram as an SVG file.
 */
function downloadMermaidSvg(svgEl) {
    const { clone } = cloneSvgForExport(svgEl);
    const svgString = new XMLSerializer().serializeToString(clone);
    const blob = new Blob(['<?xml version="1.0" encoding="UTF-8"?>\n' + svgString], { type: 'image/svg+xml' });
    triggerDownload(blob, 'mermaid-diagram.svg');
}

/**
 * Download the diagram as a PNG at the given scale factor.
 * Ensures minimum 1000px on the longest side.
 */
function downloadMermaidPng(svgEl, scaleFactor) {
    const { clone, width, height } = cloneSvgForExport(svgEl);

    let targetW = Math.round(width * scaleFactor);
    let targetH = Math.round(height * scaleFactor);

    // Ensure minimum 1000px on the longest side
    const maxDim = Math.max(targetW, targetH);
    if (maxDim < 1000) {
        const ratio = 1000 / maxDim;
        targetW = Math.round(targetW * ratio);
        targetH = Math.round(targetH * ratio);
    }

    const svgString = new XMLSerializer().serializeToString(clone);
    // Use data URL instead of blob URL to avoid tainted canvas
    const dataUrl = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgString);

    const img = new Image();
    img.onload = function () {
        const canvas = document.createElement('canvas');
        canvas.width = targetW;
        canvas.height = targetH;
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = isDarkMode ? '#1e1e1e' : '#ffffff';
        ctx.fillRect(0, 0, targetW, targetH);
        ctx.drawImage(img, 0, 0, targetW, targetH);

        canvas.toBlob(function (blob) {
            triggerDownload(blob, 'mermaid-diagram.png');
        }, 'image/png');
    };
    img.onerror = function () {
        console.error('Failed to render PNG from SVG');
    };
    img.src = dataUrl;
}

function triggerDownload(blob, filename) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 100);
}
