window.nightmareUi = window.nightmareUi || {};

window.nightmareUi.downloadTextFile = (fileName, contents, contentType) => {
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


window.nightmareUi.getLocalStorage = (key) => {
    try {
        return window.localStorage.getItem(key);
    } catch {
        return null;
    }
};

window.nightmareUi.setLocalStorage = (key, value) => {
    try {
        window.localStorage.setItem(key, value ?? "");
    } catch {
        // Storage may be unavailable in private browsing or locked-down environments.
    }
};

window.nightmareUi.importTargetFileInChunks = async (input, dotNetRef, globalMaxDepth, chunkCount) => {
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
            body: JSON.stringify({
                domains,
                globalMaxDepth: depth
            })
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
