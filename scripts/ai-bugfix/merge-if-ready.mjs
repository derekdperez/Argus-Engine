const token = process.env.GH_TOKEN;
const repo = process.env.REPO;
const prNumberStr = process.env.PULL_NUMBER;
const allowedReviewersStr = process.env.ALLOWED_REVIEWERS || '';
const callbackToken = process.env.ARGUS_AI_BUGFIX_CALLBACK_TOKEN;
const baseUrl = process.env.ARGUS_MAINTENANCE_API_BASE_URL;

if (!token || !repo || !prNumberStr) {
    console.error('Missing required environment variables.');
    process.exit(1);
}

const prNumber = parseInt(prNumberStr, 10);
const allowedReviewers = allowedReviewersStr.split(',').map(s => s.trim()).filter(s => s.length > 0);

async function github(path, options = {}) {
    const url = `https://api.github.com/repos/${repo}${path}`;
    const res = await fetch(url, {
        ...options,
        headers: {
            'Authorization': `Bearer ${token}`,
            'Accept': 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28',
            ...options.headers
        }
    });

    if (!res.ok) {
        const text = await res.text();
        throw new Error(`GitHub API ${options.method || 'GET'} ${path} failed: ${res.status} ${text}`);
    }

    // Some endpoints like merge return 204 No Content or return JSON.
    const contentType = res.headers.get('content-type');
    if (contentType && contentType.includes('application/json')) {
        return res.json();
    }
    return null;
}

async function reportCallback(runId, payload) {
    if (!callbackToken || !baseUrl) return;
    const url = `${baseUrl.replace(/\/$/, '')}/api/internal/diagnostics/ai-bug-fixes/${runId}/workflow-callback`;
    try {
        const res = await fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Authorization': `Bearer ${callbackToken}`
            },
            body: JSON.stringify(payload)
        });
        if (!res.ok) {
            console.error(`Failed to report callback to ${url}: ${res.status}`);
        }
    } catch (e) {
        console.error(`Network error reporting callback: ${e.message}`);
    }
}

async function main() {
    console.log(`Checking PR #${prNumber} for readiness...`);

    const pr = await github(`/pulls/${prNumber}`);
    const labels = pr.labels.map(l => l.name);
    
    // Extract runId from branch name: ai-bugfix/{runId}
    const branchPrefix = 'ai-bugfix/';
    let runId = null;
    if (pr.head.ref.startsWith(branchPrefix)) {
        runId = pr.head.ref.substring(branchPrefix.length);
    }

    if (!labels.includes('ai-bugfix') || !runId) {
        console.log(`PR #${prNumber} does not have 'ai-bugfix' label or proper branch format. Skipping.`);
        return;
    }

    if (pr.merged) {
        console.log(`PR #${prNumber} is already merged.`);
        return;
    }

    if (pr.state === 'closed') {
        console.log(`PR #${prNumber} is closed but not merged.`);
        return;
    }

    if (pr.mergeable !== true) {
        console.log(`PR #${prNumber} is not mergeable (mergeable state: ${pr.mergeable_state}).`);
        return;
    }

    // Check reviews
    const reviews = await github(`/pulls/${prNumber}/reviews`);
    // Find latest review per user
    const userReviews = {};
    for (const r of reviews) {
        // reviews are returned chronological, so overwriting keeps the latest
        userReviews[r.user.login] = r.state; 
    }

    let hasApprovedReview = false;
    let hasChangesRequested = false;

    for (const [user, state] of Object.entries(userReviews)) {
        // If allowedReviewers is configured, only consider their reviews
        if (allowedReviewers.length > 0 && !allowedReviewers.includes(user)) {
            continue;
        }

        if (state === 'APPROVED') {
            hasApprovedReview = true;
        } else if (state === 'CHANGES_REQUESTED') {
            hasChangesRequested = true;
        }
    }

    if (hasChangesRequested) {
        console.log('PR has requested changes from an allowed reviewer. Skipping merge.');
        return;
    }

    if (!hasApprovedReview) {
        console.log('PR does not have an APPROVED review from an allowed reviewer. Skipping merge.');
        return;
    }

    // Optional: check status checks here if not relying completely on GitHub branch protection
    // Assuming branch protection enforces checks.

    console.log(`PR #${prNumber} is approved and mergeable. Merging...`);

    try {
        const mergeRes = await github(`/pulls/${prNumber}/merge`, {
            method: 'PUT',
            body: JSON.stringify({
                commit_title: `Merge AI bug fix PR #${prNumber} (${runId})`,
                commit_message: `Automated merge of AI bug fix for run ${runId}`,
                merge_method: 'squash'
            })
        });

        console.log(`Successfully merged PR #${prNumber}. SHA: ${mergeRes.sha}`);

        await reportCallback(runId, {
            RunId: runId,
            Status: 'Merging', // Workflow 'release-main' will take it to 'Deploying' / 'Deployed'
            StatusMessage: 'PR merged successfully. Release pipeline should start shortly.',
            MergeSha: mergeRes.sha
        });

    } catch (e) {
        console.error('Failed to merge PR:', e);
        await reportCallback(runId, {
            RunId: runId,
            Status: 'Failed',
            StatusMessage: 'Failed to merge PR programmatically.',
            FailureDetail: e.message
        });
        process.exit(1);
    }
}

main().catch(err => {
    console.error('Unhandled error:', err);
    process.exit(1);
});
