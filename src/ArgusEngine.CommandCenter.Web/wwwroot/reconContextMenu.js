window.ReconContextMenu = {
    _menu: null,
    _overlay: null,
    _toast: null,
    _toastTimer: null,
    _targetId: null,
    _subdomainKey: null,
    _rowType: null,

    init: function () {
        document.addEventListener('contextmenu', function (e) {
            var row = e.target.closest('tr');
            if (!row) return;

            var targetId = ReconContextMenu._getData(row, 'target-id');
            if (!targetId) return;

            e.preventDefault();

            ReconContextMenu._targetId = targetId;
            ReconContextMenu._subdomainKey = ReconContextMenu._getData(row, 'subdomain-key');
            ReconContextMenu._rowType = ReconContextMenu._subdomainKey ? 'subdomain' : 'target';
            ReconContextMenu._show(e.clientX, e.clientY);
        });
    },

    _getData: function (row, attr) {
        var el = row.querySelector('[data-' + attr + ']');
        return el ? el.getAttribute('data-' + attr) : null;
    },

    _show: function (x, y) {
        if (!this._menu) {
            this._menu = document.createElement('div');
            this._menu.className = 'recon-custom-menu';
            this._overlay = document.createElement('div');
            this._overlay.className = 'recon-menu-overlay';
            this._overlay.onclick = function () { ReconContextMenu._hide(); };
            document.body.appendChild(this._overlay);
            document.body.appendChild(this._menu);
        }

        if (!this._toast) {
            this._toast = document.createElement('div');
            this._toast.className = 'recon-toast';
            document.body.appendChild(this._toast);
        }

        this._menu.innerHTML = this._rowType === 'subdomain'
            ? this._subdomainTemplate()
            : this._targetTemplate();

        this._menu.style.display = 'block';
        this._menu.style.left = x + 'px';
        this._menu.style.top = y + 'px';
        this._overlay.style.display = 'block';
    },

    _targetTemplate: function () {
        return '<div class="recon-menu-header">Target actions</div>' +
            '<button class="recon-menu-item" onclick="ReconContextMenu._action(\'recon\')">Assign Recon Orchestrator</button>' +
            '<button class="recon-menu-item" onclick="ReconContextMenu._action(\'enumerate\')">Enumerate Subdomains</button>' +
            '<button class="recon-menu-item" onclick="ReconContextMenu._action(\'spider\')">Spider</button>';
    },

    _subdomainTemplate: function () {
        return '<div class="recon-menu-header">Subdomain actions</div>' +
            '<button class="recon-menu-item" onclick="ReconContextMenu._action(\'spider-subdomain\')">Spider Subdomain</button>';
    },

    _hide: function () {
        if (this._menu) this._menu.style.display = 'none';
        if (this._overlay) this._overlay.style.display = 'none';
    },

    _toast: function (msg, isError) {
        var el = document.querySelector('.recon-toast');
        if (!el) return;
        el.textContent = msg;
        el.className = 'recon-toast' + (isError ? ' recon-toast-error' : ' recon-toast-success');
        el.style.display = 'block';
        clearTimeout(this._toastTimer);
        this._toastTimer = setTimeout(function () { el.style.display = 'none'; }, 4000);
    },

    _action: async function (action) {
        var id = this._targetId;
        var subdomain = this._subdomainKey;
        this._hide();
        if (!id) return;

        this._toast('Processing...', false);

        try {
            var resp, data;

            switch (action) {
                case 'recon':
                    resp = await fetch('/api/recon-agent/targets/' + encodeURIComponent(id) + '/attach', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ attachedBy: 'context-menu', configuration: null })
                    });
                    if (resp.ok) {
                        this._toast('Recon orchestrator assigned to target', false);
                    } else {
                        var err = await resp.text();
                        this._toast('Failed: ' + err, true);
                    }
                    break;

                case 'enumerate':
                    resp = await fetch('/api/ops/subdomain-enum/restart', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ targetIds: [id], allTargets: false })
                    });
                    if (resp.ok) {
                        data = await resp.json();
                        this._toast('Enumerate queued (' + (data.jobsQueued || data.jobsQueued === 0 ? data.jobsQueued + ' jobs' : 'done') + ')', false);
                    } else {
                        this._toast('Enumerate failed', true);
                    }
                    break;

                case 'spider':
                    resp = await fetch('/api/ops/spider/restart', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ targetIds: [id], allTargets: false })
                    });
                    if (resp.ok) {
                        data = await resp.json();
                        this._toast('Spider queued (' + (data.rootSeedsQueued || 0) + ' seeds)', false);
                    } else {
                        this._toast('Spider failed', true);
                    }
                    break;

                case 'spider-subdomain':
                    resp = await fetch('/api/ops/spider/subdomains/restart', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ targetIds: [id], subdomains: [subdomain] })
                    });
                    if (resp.ok) {
                        data = await resp.json();
                        this._toast('Spider queued for subdomain', false);
                    } else {
                        this._toast('Spider subdomain failed', true);
                    }
                    break;
            }
        } catch (ex) {
            this._toast('Error: ' + ex.message, true);
        }
    }
};
