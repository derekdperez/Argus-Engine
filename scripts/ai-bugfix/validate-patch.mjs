import path from 'path';

export function validatePatch(patch, bundle) {
    const errors = [];
    const maxFiles = bundle.maxFiles || 30;
    const maxFileSizeBytes = bundle.maxFileSizeBytes || 512000;
    const allowedPrefixes = bundle.allowedPathPrefixes || [];

    if (!patch.files || !Array.isArray(patch.files)) {
        errors.push('Patch does not contain a valid files array.');
        return errors;
    }

    if (patch.files.length > maxFiles) {
        errors.push(`Patch contains ${patch.files.length} files, exceeding the limit of ${maxFiles}.`);
    }

    for (const file of patch.files) {
        // 1. Path must be relative and not contain '..'
        if (path.isAbsolute(file.path)) {
            errors.push(`Path is absolute: ${file.path}`);
            continue;
        }
        
        const normalized = path.normalize(file.path);
        if (normalized.startsWith('..') || normalized.startsWith('/') || normalized.startsWith('\\')) {
            errors.push(`Path escapes repository root: ${file.path}`);
            continue;
        }

        // 2. Check allowed prefixes
        let allowed = false;
        for (const prefix of allowedPrefixes) {
            const normalizedPrefix = path.normalize(prefix);
            if (normalized.startsWith(normalizedPrefix) || normalized === normalizedPrefix) {
                allowed = true;
                break;
            }
        }
        if (!allowed) {
            errors.push(`Path is not in the allowed prefixes list: ${file.path}`);
        }

        // 3. Denylist checks
        if (normalized.includes('.git')) {
            errors.push(`Path contains .git directory: ${file.path}`);
        }
        
        if (normalized === path.normalize('.github/workflows/release-main.yml')) {
            errors.push(`Modifying the main release workflow is prohibited: ${file.path}`);
        }

        // 4. Content size check
        if (file.content) {
            const size = Buffer.byteLength(file.content, 'utf8');
            if (size > maxFileSizeBytes) {
                errors.push(`File ${file.path} is ${size} bytes, exceeding the limit of ${maxFileSizeBytes} bytes.`);
            }
        }
    }

    return errors;
}
