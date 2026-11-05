// The whole front end. No framework, no build step, no CDN - it also runs with no egress.
//
// The server speaks in page units with the origin at the top-left corner, so the only
// conversion here is between those page units and the pixels of the rendered page image.

const state = {
    document: null,
    info: null,
    page: 1,
    annotations: [],
    selected: null,
};

const el = {
    documents: document.getElementById('documents'),
    page: document.getElementById('page'),
    overlay: document.getElementById('overlay'),
    stage: document.getElementById('stage'),
    pageLabel: document.getElementById('pageLabel'),
    prev: document.getElementById('prev'),
    next: document.getElementById('next'),
    list: document.getElementById('list'),
    empty: document.getElementById('empty'),
    status: document.getElementById('status'),
};

function status(text) {
    el.status.textContent = text || '';
}

async function api(path, options) {
    const response = await fetch(path, options);
    if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error || `${response.status} ${response.statusText}`);
    }
    return response.json();
}

// ------------------------------------------------------------------ loading

async function start() {
    const names = await api('/api/documents');
    el.documents.innerHTML = names.map((n) => `<option value="${n}">${n}</option>`).join('');

    if (names.length === 0) {
        status('No documents in the storage folder.');
        return;
    }

    await open(names[0]);
}

async function open(name) {
    state.document = name;
    state.info = await api(`/api/documents/${encodeURIComponent(name)}/info`);
    state.page = 1;
    await showPage();
}

async function showPage() {
    const { document: name, info, page } = state;
    el.pageLabel.textContent = `page ${page} of ${info.pageCount}`;
    el.page.src = `/api/documents/${encodeURIComponent(name)}/pages/${page}/image?v=${Date.now()}`;
    await new Promise((resolve) => {
        el.page.onload = resolve;
        el.page.onerror = resolve;
    });
    await reload();
}

async function reload() {
    state.annotations = await api(`/api/documents/${encodeURIComponent(state.document)}/annotations`);
    render();
}

// ------------------------------------------------------------------ geometry

function pageSize() {
    const info = state.info.pages.find((p) => p.number === state.page);
    return info || { width: 1, height: 1 };
}

// page units -> pixels of the displayed image
function toPixels(box) {
    const size = pageSize();
    const scaleX = el.page.clientWidth / size.width;
    const scaleY = el.page.clientHeight / size.height;
    return {
        x: box.x * scaleX,
        y: box.y * scaleY,
        width: box.width * scaleX,
        height: box.height * scaleY,
    };
}

// ------------------------------------------------------------------ drawing

function render() {
    const canvas = el.overlay;
    canvas.width = el.page.clientWidth;
    canvas.height = el.page.clientHeight;

    const context = canvas.getContext('2d');
    context.clearRect(0, 0, canvas.width, canvas.height);

    const onPage = state.annotations.filter((a) => a.page === state.page);

    for (const annotation of onPage) {
        const box = toPixels(annotation);
        const active = annotation.id === state.selected;

        context.fillStyle = active ? 'rgba(255, 170, 0, 0.35)' : 'rgba(255, 200, 60, 0.25)';
        context.strokeStyle = active ? '#c77700' : '#e0a000';
        context.lineWidth = active ? 2 : 1;
        context.fillRect(box.x, box.y, box.width, box.height);
        context.strokeRect(box.x, box.y, box.width, box.height);
    }

    renderList(onPage);
}

function renderList(onPage) {
    el.empty.hidden = onPage.length > 0;
    el.list.innerHTML = '';

    for (const annotation of onPage) {
        const item = document.createElement('li');
        if (annotation.id === state.selected) {
            item.className = 'active';
        }

        const replies = (annotation.replies || [])
            .map((r) => `<li>${escapeHtml(r.user || 'anonymous')}: ${escapeHtml(r.comment || '')}</li>`)
            .join('');

        item.innerHTML = `
            <div class="meta"><span>#${annotation.id} ${escapeHtml(annotation.type)}</span><span>${escapeHtml(annotation.user || '')}</span></div>
            <p class="message">${escapeHtml(annotation.message || '(no comment)')}</p>
            ${replies ? `<ul class="replies">${replies}</ul>` : ''}
            <div class="actions">
                <input type="text" placeholder="Reply..." />
                <button type="button" data-action="reply">Reply</button>
                <button type="button" data-action="delete">Delete</button>
            </div>`;

        item.addEventListener('click', () => {
            state.selected = annotation.id;
            render();
        });

        item.querySelector('[data-action="reply"]').addEventListener('click', async (event) => {
            event.stopPropagation();
            const input = item.querySelector('input');
            if (!input.value.trim()) {
                return;
            }
            await send(`/api/documents/${encodeURIComponent(state.document)}/annotations/${annotation.id}`, 'PUT', {
                reply: input.value.trim(),
                user: 'Reviewer',
            });
        });

        item.querySelector('[data-action="delete"]').addEventListener('click', async (event) => {
            event.stopPropagation();
            await send(`/api/documents/${encodeURIComponent(state.document)}/annotations/${annotation.id}`, 'DELETE');
        });

        el.list.appendChild(item);
    }
}

async function send(url, method, body) {
    try {
        status('Saving...');
        state.annotations = await api(url, {
            method,
            headers: body ? { 'Content-Type': 'application/json' } : undefined,
            body: body ? JSON.stringify(body) : undefined,
        });
        status('Saved');
        render();
    } catch (error) {
        status(error.message);
    }
}

function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}

// ------------------------------------------------------------------ input

let drag = null;

el.overlay.addEventListener('pointerdown', (event) => {
    el.overlay.setPointerCapture(event.pointerId);
    const rect = el.overlay.getBoundingClientRect();
    drag = { x: event.clientX - rect.left, y: event.clientY - rect.top, width: 0, height: 0 };
});

el.overlay.addEventListener('pointermove', (event) => {
    if (!drag) {
        return;
    }

    const rect = el.overlay.getBoundingClientRect();
    drag.width = event.clientX - rect.left - drag.x;
    drag.height = event.clientY - rect.top - drag.y;

    render();
    const context = el.overlay.getContext('2d');
    context.strokeStyle = '#c77700';
    context.setLineDash([4, 3]);
    context.strokeRect(drag.x, drag.y, drag.width, drag.height);
    context.setLineDash([]);
});

el.overlay.addEventListener('pointerup', async () => {
    if (!drag) {
        return;
    }

    const box = normalize(drag);
    drag = null;

    if (box.width < 6 || box.height < 6) {
        render();
        return;
    }

    const message = window.prompt('Comment');
    if (message === null) {
        render();
        return;
    }

    await send(`/api/documents/${encodeURIComponent(state.document)}/annotations`, 'POST', {
        page: state.page,
        x: box.x,
        y: box.y,
        width: box.width,
        height: box.height,
        imageWidth: el.page.clientWidth,
        imageHeight: el.page.clientHeight,
        message,
        user: 'Reviewer',
    });
});

function normalize(box) {
    return {
        x: box.width < 0 ? box.x + box.width : box.x,
        y: box.height < 0 ? box.y + box.height : box.y,
        width: Math.abs(box.width),
        height: Math.abs(box.height),
    };
}

el.documents.addEventListener('change', () => open(el.documents.value));
el.prev.addEventListener('click', () => { if (state.page > 1) { state.page--; showPage(); } });
el.next.addEventListener('click', () => { if (state.page < state.info.pageCount) { state.page++; showPage(); } });
window.addEventListener('resize', () => render());

start().catch((error) => status(error.message));
