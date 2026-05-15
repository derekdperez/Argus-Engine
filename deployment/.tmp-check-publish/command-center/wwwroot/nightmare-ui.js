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
