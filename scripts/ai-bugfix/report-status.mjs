const runId = process.env.ARGUS_AI_BUGFIX_RUN_ID;
const token = process.env.ARGUS_AI_BUGFIX_CALLBACK_TOKEN;
const baseUrl = process.env.ARGUS_MAINTENANCE_API_BASE_URL;

if (!runId || !token || !baseUrl) {
    console.error('Missing required environment variables (ARGUS_AI_BUGFIX_RUN_ID, ARGUS_AI_BUGFIX_CALLBACK_TOKEN, ARGUS_MAINTENANCE_API_BASE_URL)');
    process.exit(1);
}

const status = process.env.STATUS;
const statusMsg = process.env.STATUS_MSG;
const branch = process.env.BRANCH;
const prNumberStr = process.env.PR_NUMBER;
const prUrl = process.env.PR_URL;
const workflowRunIdStr = process.env.WORKFLOW_RUN_ID;
const workflowUrl = process.env.WORKFLOW_URL;
const failureDetail = process.env.FAILURE_DETAIL;

if (!status) {
    console.error('STATUS environment variable is required.');
    process.exit(1);
}

const payload = {
    RunId: runId,
    Status: status,
    StatusMessage: statusMsg,
    Branch: branch,
    PullRequestNumber: prNumberStr ? parseInt(prNumberStr, 10) : null,
    PullRequestUrl: prUrl,
    WorkflowRunId: workflowRunIdStr ? parseInt(workflowRunIdStr, 10) : null,
    WorkflowUrl: workflowUrl,
    FailureDetail: failureDetail
};

const callbackUrl = `${baseUrl.replace(/\/$/, '')}/api/internal/diagnostics/ai-bug-fixes/${runId}/workflow-callback`;

console.log(`Reporting status '${status}' to ${callbackUrl}`);

fetch(callbackUrl, {
    method: 'POST',
    headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify(payload)
})
.then(res => {
    if (!res.ok) {
        return res.text().then(text => {
            console.error(`Callback failed: ${res.status} ${text}`);
            process.exit(1);
        });
    }
    console.log('Status reported successfully.');
})
.catch(err => {
    console.error('Network error during callback:', err);
    process.exit(1);
});
