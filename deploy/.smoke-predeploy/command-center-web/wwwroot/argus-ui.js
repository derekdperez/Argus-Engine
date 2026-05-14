window.argusUi = window.argusUi || {};

window.argusUi.downloadTextFile = (fileName, contents, contentType) => {
    const blob = new Blob([contents ?? ""], { type: contentType || "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName || "download.txt";
    anchor.style.display = "none";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
};

window.argusUi.getLocalStorage = (key) => {
    try {
        return window.localStorage.getItem(key);
    } catch {
        return null;
    }
};

window.argusUi.setLocalStorage = (key, value) => {
    try {
        window.localStorage.setItem(key, value ?? "");
    } catch {
        // Storage may be unavailable in private browsing or locked-down environments.
    }
};

window.argusUi.importTargetFileInChunks = async (input, dotNetRef, globalMaxDepth, chunkCount) => {
    const file = input?.files?.[0];
    if (!file) {
        throw new Error("Choose a target file first.");
    }

    const chunksTotal = Math.max(1, chunkCount || 20);
    const text = await file.text();
    const lines = text.split(/\r\n|\n|\r/);
    const depth = Number.isFinite(globalMaxDepth) && globalMaxDepth > 0 ? globalMaxDepth : 12;

    let totalCreated = 0;
    let totalSkippedAlreadyExist = 0;
    let totalSkippedEmptyOrInvalid = 0;
    let totalSkippedDuplicateInBatch = 0;

    for (let index = 0; index < chunksTotal; index++) {
        const start = Math.floor(lines.length * index / chunksTotal);
        const end = Math.floor(lines.length * (index + 1) / chunksTotal);
        const domains = lines.slice(start, end);

        const response = await fetch("/api/targets/bulk", {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "Accept": "application/json"
            },
            body: JSON.stringify({ domains, globalMaxDepth: depth })
        });

        if (!response.ok) {
            const message = await response.text();
            throw new Error(message || `Chunk ${index + 1} failed with HTTP ${response.status}.`);
        }

        const result = await response.json();
        const created = result.created ?? result.Created ?? 0;
        const skippedAlreadyExist = result.skippedAlreadyExist ?? result.SkippedAlreadyExist ?? 0;
        const skippedEmptyOrInvalid = result.skippedEmptyOrInvalid ?? result.SkippedEmptyOrInvalid ?? 0;
        const skippedDuplicateInBatch = result.skippedDuplicateInBatch ?? result.SkippedDuplicateInBatch ?? 0;

        totalCreated += created;
        totalSkippedAlreadyExist += skippedAlreadyExist;
        totalSkippedEmptyOrInvalid += skippedEmptyOrInvalid;
        totalSkippedDuplicateInBatch += skippedDuplicateInBatch;

        await dotNetRef.invokeMethodAsync(
            "OnBulkImportChunkCompleted",
            index + 1,
            chunksTotal,
            created,
            skippedAlreadyExist,
            skippedEmptyOrInvalid,
            skippedDuplicateInBatch,
            totalCreated,
            totalSkippedAlreadyExist,
            totalSkippedEmptyOrInvalid,
            totalSkippedDuplicateInBatch);
    }

    return {
        created: totalCreated,
        skippedAlreadyExist: totalSkippedAlreadyExist,
        skippedEmptyOrInvalid: totalSkippedEmptyOrInvalid,
        skippedDuplicateInBatch: totalSkippedDuplicateInBatch,
        Created: totalCreated,
        SkippedAlreadyExist: totalSkippedAlreadyExist,
        SkippedEmptyOrInvalid: totalSkippedEmptyOrInvalid,
        SkippedDuplicateInBatch: totalSkippedDuplicateInBatch
    };
};

(() => {
    const loadingTerms = [
        "loading",
        "refreshing",
        "running diagnostics",
        "generating",
        "connecting",
        "not loaded",
        "waiting for system events"
    ];

    const activeLoadingTerms = [
        "loading",
        "refreshing",
        "running diagnostics",
        "generating",
        "connecting",
        "not loaded"
    ];

    const controlSelector = [
        "button",
        ".btn",
        ".loading-inline",
        ".muted",
        ".badge",
        ".realtime-status",
        ".status-dot"
    ].join(",");

    const valueCardSelector = [
        ".stat-card",
        ".metric-card",
        ".ops-page-lite .metric-card"
    ].join(",");

    const valueSelector = [
        ".stat-value",
        "strong"
    ].join(",");

    const tableSelector = [
        ".grid",
        ".ops-table",
        ".rz-datatable-table",
        ".rz-grid-table"
    ].join(",");

    let observerStarted = false;
    let scanScheduled = false;
    let lastScan = 0;

    function normalizedText(element) {
        return (element?.innerText || element?.textContent || "")
            .replace(/\s+/g, " ")
            .trim()
            .toLowerCase();
    }

    function isLoadingText(text) {
        return loadingTerms.some(term => text.includes(term));
    }

    function hasActiveLoadingText(text) {
        return activeLoadingTerms.some(term => text.includes(term));
    }

    function isZeroish(text) {
        const normalized = (text || "")
            .replace(/\u00a0/g, " ")
            .replace(/,/g, "")
            .replace(/\s+/g, " ")
            .trim()
            .toLowerCase();

        if (!normalized) {
            return false;
        }

        if (/^0$/.test(normalized)) {
            return true;
        }

        if (/^0\s*(rows|row|requests|request|assets|asset|targets|target|subdomains|subdomain|queued|running|pending|active|events|event|containers|container|workers|worker)$/i.test(normalized)) {
            return true;
        }

        if (/^0\s*\/\s*0(\s|$)/.test(normalized)) {
            return true;
        }

        if (/^0\s+queued\s*\/\s*0\s+sent\/min$/.test(normalized)) {
            return true;
        }

        return false;
    }

    function tagLoadingControls(root, pageLoading) {
        root.querySelectorAll(".argus-loading-control,.argus-loading-text").forEach(element => {
            const text = normalizedText(element);
            if (!isLoadingText(text)) {
                element.classList.remove("argus-loading-control", "argus-loading-text");
                element.removeAttribute("aria-busy");
            }
        });

        if (!pageLoading) {
            return;
        }

        root.querySelectorAll(controlSelector).forEach(element => {
            if (element.closest(".argus-value-loading")) {
                return;
            }

            const text = normalizedText(element);
            if (!text || text.length > 80 || !isLoadingText(text)) {
                return;
            }

            if (element.matches("button,.btn")) {
                element.classList.add("argus-loading-control");
            } else {
                element.classList.add("argus-loading-text");
            }

            element.setAttribute("aria-busy", "true");
        });
    }

    function tagZeroValueCards(root, pageLoading) {
        root.querySelectorAll(".argus-value-loading").forEach(card => {
            if (!pageLoading) {
                card.classList.remove("argus-value-loading");
                card.removeAttribute("aria-busy");
            }
        });

        if (!pageLoading) {
            return;
        }

        root.querySelectorAll(valueCardSelector).forEach(card => {
            const value = card.querySelector(valueSelector);
            if (!value) {
                return;
            }

            const labelText = normalizedText(card);
            const valueText = normalizedText(value);

            if (!isZeroish(valueText)) {
                card.classList.remove("argus-value-loading");
                card.removeAttribute("aria-busy");
                return;
            }

            // Only mask zeros while a page or sibling control is actively loading. This keeps genuine
            // zeroes visible after a data load completes.
            if (!hasActiveLoadingText(document.body.innerText || "") && !hasActiveLoadingText(labelText)) {
                return;
            }

            card.classList.add("argus-value-loading");
            card.setAttribute("aria-busy", "true");
        });
    }

    function tagEmptyLazyTables(root, pageLoading) {
        if (!pageLoading) {
            root.querySelectorAll(".argus-loading-row").forEach(row => row.classList.remove("argus-loading-row"));
            return;
        }

        root.querySelectorAll(tableSelector).forEach(table => {
            const rows = Array.from(table.querySelectorAll("tbody tr"));
            if (rows.length !== 1) {
                return;
            }

            const rowText = normalizedText(rows[0]);
            if (rowText && !isLoadingText(rowText) && rowText !== "0") {
                return;
            }

            rows[0].classList.add("argus-loading-row");
        });
    }

    function scan() {
        scanScheduled = false;

        if (!document.body) {
            return;
        }

        // Avoid scanning too aggressively during Blazor render bursts.
        const now = Date.now();
        if (now - lastScan < 80) {
            scheduleScan(100);
            return;
        }

        lastScan = now;

        const bodyText = normalizedText(document.body);
        const pageLoading = hasActiveLoadingText(bodyText);

        document.body.classList.toggle("argus-page-loading", pageLoading);
        tagLoadingControls(document, pageLoading);
        tagZeroValueCards(document, pageLoading);
        tagEmptyLazyTables(document, pageLoading);
    }

    function scheduleScan(delay = 0) {
        if (scanScheduled) {
            return;
        }

        scanScheduled = true;
        window.setTimeout(scan, delay);
    }

    function startObserver() {
        if (observerStarted || !document.body) {
            return;
        }

        observerStarted = true;
        const observer = new MutationObserver(() => scheduleScan(50));
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            characterData: true
        });

        scheduleScan(0);
        window.addEventListener("pageshow", () => scheduleScan(0));
        document.addEventListener("visibilitychange", () => {
            if (!document.hidden) {
                scheduleScan(0);
            }
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", startObserver, { once: true });
    } else {
        startObserver();
    }
})();
