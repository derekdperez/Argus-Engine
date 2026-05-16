window.ReconContextMenu = {
    _initialized: false,
    _menu: null,
    _overlay: null,
    _toastElement: null,
    _toastTimer: null,
    _panel: null,
    _summary: null,
    _list: null,
    _targetId: null,
    _subdomainKey: null,
    _rowType: null,

    init: function () {
        if (this._initialized) {
            this._ensureFeedbackPanel();
            return;
        }

        this._initialized = true;
        this._ensureFeedbackPanel();

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

        this._ensureFeedbackPanel();
        this._ensureToast();

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

    _ensureToast: function () {
        if (this._toastElement && document.body.contains(this._toastElement)) {
            return this._toastElement;
        }

        this._toastElement = document.querySelector('.recon-toast');
        if (!this._toastElement) {
            this._toastElement = document.createElement('div');
            this._toastElement.className = 'recon-toast';
            document.body.appendChild(this._toastElement);
        }

        return this._toastElement;
    },

    _showToast: function (msg, isError) {
        var el = this._ensureToast();
        el.textContent = msg;
        el.className = 'recon-toast' + (isError ? ' recon-toast-error' : ' recon-toast-success');
        el.style.display = 'block';
        clearTimeout(this._toastTimer);
        this._toastTimer = setTimeout(function () { el.style.display = 'none'; }, 4000);
    },

    _ensureFeedbackPanel: function () {
        this._panel = document.getElementById('recon-action-results');
        this._summary = document.getElementById('recon-action-summary');
        this._list = document.getElementById('recon-action-list');

        if (!this._panel) {
            this._panel = document.createElement('div');
            this._panel.id = 'recon-action-results';
            this._panel.className = 'recon-action-collapsed';
            document.body.appendChild(this._panel);

            var header = document.createElement('div');
            header.className = 'recon-action-panel-header';
            header.innerHTML = '<div class="recon-action-summary-area"><span id="recon-action-summary">Actions</span></div><div class="recon-action-header-btns"><button id="recon-action-clear" type="button" class="cc-btn cc-btn-small">Clear</button></div>';

            this._list = document.createElement('div');
            this._list.id = 'recon-action-list';
            this._list.className = 'recon-action-list';

            this._panel.appendChild(header);
            this._panel.appendChild(this._list);
            this._summary = document.getElementById('recon-action-summary');
        }

        var clearButton = document.getElementById('recon-action-clear');
        if (clearButton && clearButton.getAttribute('data-recon-bound') !== 'true') {
            clearButton.setAttribute('data-recon-bound', 'true');
            clearButton.addEventListener('click', function () {
                ReconContextMenu._clearResults();
            });
        }
    },

    _showPanel: function () {
        var panel = document.getElementById('recon-action-results');
        if (panel) {
            panel.classList.remove('recon-action-collapsed');
            panel.classList.add('recon-action-expanded');
        }
        clearTimeout(this._hideTimer);
    },

    _scheduleHide: function () {
        clearTimeout(this._hideTimer);
        this._hideTimer = setTimeout(function () {
            var panel = document.getElementById('recon-action-results');
            if (panel) {
                panel.classList.remove('recon-action-expanded');
                panel.classList.add('recon-action-collapsed');
            }
        }, 5000);
    },

    _toggle: function () {
        var panel = document.getElementById('recon-action-results');
        if (!panel) return;
        var isCollapsed = panel.classList.contains('recon-action-collapsed');
        panel.classList.toggle('recon-action-collapsed');
        panel.classList.toggle('recon-action-expanded');
        var btn = document.getElementById('recon-action-expand');
        if (btn) btn.textContent = isCollapsed ? 'Collapse' : 'Expand';
    },

    _clearResults: function () {
        this._ensureFeedbackPanel();
        if (this._list) {
            this._list.innerHTML = '';
        }
        if (this._summary) {
            this._summary.textContent = 'Actions';
        }
        clearTimeout(this._hideTimer);
        var panel = document.getElementById('recon-action-results');
        if (panel) {
            panel.classList.remove('recon-action-expanded');
            panel.classList.add('recon-action-collapsed');
        }
    },

    _startResult: function (action, targetId, subdomain) {
        this._ensureFeedbackPanel();
        this._showPanel();

        var entry = document.createElement('div');
        entry.className = 'recon-action-entry recon-action-pending';

        var top = document.createElement('div');
        top.className = 'recon-action-entry-top';

        var title = document.createElement('h3');
        title.textContent = this._actionLabel(action);

        var state = document.createElement('span');
        state.className = 'recon-action-state';
        state.textContent = 'Processing';

        top.appendChild(title);
        top.appendChild(state);

        var meta = document.createElement('div');
        meta.className = 'recon-action-meta';
        meta.textContent = this._targetLabel(targetId, subdomain);

        var detail = document.createElement('pre');
        detail.className = 'recon-action-detail';
        detail.textContent = 'Request started at ' + new Date().toLocaleTimeString() + '.';

        entry.appendChild(top);
        entry.appendChild(meta);
        entry.appendChild(detail);
        entry._state = state;
        entry._detail = detail;

        if (this._list) {
            this._list.prepend(entry);
            while (this._list.children.length > 12) {
                this._list.removeChild(this._list.lastElementChild);
            }
        }

        if (this._summary) {
            this._summary.textContent = this._actionLabel(action) + ' is processing.';
        }

        return entry;
    },

    _finishResult: function (entry, isError, message, detail) {
        if (!entry) return;

        entry.className = 'recon-action-entry ' + (isError ? 'recon-action-error' : 'recon-action-success');
        if (entry._state) {
            entry._state.textContent = isError ? 'Failed' : 'Completed';
        }
        if (entry._detail) {
            entry._detail.textContent = message + (detail ? '\n' + detail : '');
        }
        if (this._summary) {
            this._summary.textContent = message;
        }

        this._scheduleHide();
    },

    _action: async function (action) {
        var id = this._targetId;
        var subdomain = this._subdomainKey;
        this._hide();

        var entry = this._startResult(action, id, subdomain);

        if (!id) {
            var missingMessage = 'Action could not start because the selected row did not expose a target id.';
            this._finishResult(entry, true, missingMessage, '');
            this._showToast(missingMessage, true);
            return;
        }

        this._showToast('Processing ' + this._actionLabel(action).toLowerCase() + '...', false);

        try {
            var resp;

            switch (action) {
                case 'recon':
                    resp = await fetch('/api/recon-agent/targets/' + encodeURIComponent(id) + '/attach', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ attachedBy: 'context-menu', configuration: null })
                    });
                    await this._recordResponse(entry, resp, 'Recon orchestrator assigned to target.', 'Recon orchestrator assignment failed.');
                    break;

                case 'enumerate':
                    resp = await fetch('/api/ops/subdomain-enum/restart', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ targetIds: [id], allTargets: false })
                    });
                    await this._recordResponse(entry, resp, 'Subdomain enumeration request completed.', 'Subdomain enumeration request failed.', 'workerScaleSucceeded');
                    break;

                case 'spider':
                    resp = await fetch('/api/ops/spider/restart', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ targetIds: [id], allTargets: false })
                    });
                    await this._recordResponse(entry, resp, 'Spider request completed.', 'Spider request failed.', 'workerScaleSucceeded');
                    break;

                case 'spider-subdomain':
                    resp = await fetch('/api/ops/spider/subdomains/restart', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ targetIds: [id], subdomains: [subdomain] })
                    });
                    await this._recordResponse(entry, resp, 'Subdomain spider request completed.', 'Subdomain spider request failed.', 'workerScaleSucceeded');
                    break;

                default:
                    this._finishResult(entry, true, 'Unknown action: ' + action, '');
                    this._showToast('Unknown action', true);
                    break;
            }
        } catch (ex) {
            var errorMessage = 'Request error: ' + ex.message;
            this._finishResult(entry, true, errorMessage, '');
            this._showToast(errorMessage, true);
        }
    },

    _recordResponse: async function (entry, resp, successMessage, failureMessage, partialKey) {
        var result = await this._readResponse(resp);
        var detail = this._formatResponse(resp, result);

        if (!resp.ok) {
            var message = failureMessage + ' HTTP ' + resp.status + ' ' + (resp.statusText || '').trim();
            this._finishResult(entry, true, message, detail);
            this._showToast(failureMessage, true);
            return;
        }

        var partialFailure = false;
        if (partialKey && result) {
            var val = result[partialKey];
            partialFailure = val === false || val === 'false';
        }

        if (partialFailure) {
            partialFailure = '(' + partialKey + ': false)\n' + detail;
            this._finishResult(entry, true, successMessage + ' (worker scale failed)', partialFailure);
            this._showToast(successMessage + ' (worker scale failed)', true);
        } else {
            this._finishResult(entry, false, successMessage, detail);
            this._showToast(successMessage, false);
        }
    },

    _readResponse: async function (resp) {
        var text = await resp.text();
        var data = null;

        if (text) {
            try {
                data = JSON.parse(text);
            } catch {
                data = null;
            }
        }

        return { text: text, data: data };
    },

    _formatResponse: function (resp, result) {
        var lines = ['HTTP ' + resp.status + ' ' + (resp.statusText || '').trim()];

        var summary = this._summarizePayload(result.data);
        if (summary) {
            lines.push(summary);
        } else if (result.text) {
            lines.push(this._truncate(result.text, 1200));
        } else {
            lines.push('No response body.');
        }

        return lines.join('\n');
    },

    _summarizePayload: function (payload) {
        if (!payload || typeof payload !== 'object') {
            return '';
        }

        var keys = [
            'status',
            'message',
            'jobsQueued',
            'rootSeedsQueued',
            'workerScale',
            'workerScaleSucceeded',
            'queued',
            'created',
            'updated',
            'error',
            'detail'
        ];

        var lines = [];
        for (var i = 0; i < keys.length; i++) {
            var key = keys[i];
            if (Object.prototype.hasOwnProperty.call(payload, key)) {
                lines.push(key + ': ' + this._formatValue(payload[key]));
            }
        }

        if (lines.length === 0) {
            lines.push(this._truncate(JSON.stringify(payload, null, 2), 1200));
        }

        return lines.join('\n');
    },

    _formatValue: function (value) {
        if (value === null || value === undefined) {
            return '';
        }

        if (typeof value === 'object') {
            return this._truncate(JSON.stringify(value), 500);
        }

        return String(value);
    },

    _truncate: function (value, maxLength) {
        if (!value || value.length <= maxLength) {
            return value || '';
        }

        return value.slice(0, maxLength - 1) + '...';
    },

    _actionLabel: function (action) {
        switch (action) {
            case 'recon':
                return 'Assign Recon Orchestrator';
            case 'enumerate':
                return 'Enumerate Subdomains';
            case 'spider':
                return 'Spider Target';
            case 'spider-subdomain':
                return 'Spider Subdomain';
            default:
                return action || 'Action';
        }
    },

    _targetLabel: function (targetId, subdomain) {
        var parts = [];
        if (targetId) {
            parts.push('Target ' + targetId);
        }
        if (subdomain) {
            parts.push('Subdomain ' + subdomain);
        }

        return parts.length > 0 ? parts.join(' | ') : 'No target selected';
    }
};
