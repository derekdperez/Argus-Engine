Argus Engine Quick Deploy Web Patch

Files:
- deploy/quick-deploy-web.sh
  Adds the super-fast deployment path. It publishes command-center-web, copies the publish output into the running container, and restarts only command-center-web.

- deploy/apply-quick-deploy-web-option.sh
  Idempotently patches deploy/deploy.sh to add:
    [Q] Quick Deploy
    [1] Deploy Web App Only
  It also adds CLI aliases:
    ./deploy/deploy.sh q
    ./deploy/deploy.sh quick-web

Apply:
  unzip argus-engine-quick-deploy-web.zip -d /path/to/argus-engine
  cd /path/to/argus-engine
  bash deploy/apply-quick-deploy-web-option.sh

Use:
  ./deploy/deploy.sh
  # press Q, then 1

  # or:
  ./deploy/deploy.sh q

Notes:
- Quick Deploy requires command-center-web to already be running.
- It intentionally refuses Dockerfile/image recipe or compose/runtime config changes.
- Use normal deploy for first-time startup, package changes, API changes, worker changes, schema changes, or infrastructure changes.
