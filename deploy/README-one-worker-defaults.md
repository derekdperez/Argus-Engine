# One-worker deployment defaults

This overlay normalizes deployment defaults so a fresh deployment starts exactly one of each worker type:

- worker-spider
- worker-http-requester
- worker-enum
- worker-portscan
- worker-highvalue
- worker-techid

Run once from the repository root:

```bash
bash deploy/apply-one-worker-defaults.sh
```

Then inspect and commit the modified deployment files:

```bash
git diff
git add deploy
git commit -m "Default deployments to one worker of each type"
```

Redeploy locally:

```bash
ARGUS_NO_UI=1 \
argus_DEPLOY_MODE=image \
argus_BUILD_SEQUENTIAL=1 \
argus_BUILD_TIMEOUT_MIN=0 \
bash deploy/deploy.sh --image
```

The patcher is intentionally idempotent and preserves a `*.one-worker-defaults.bak` copy of each file it changes the first time it runs.
