# GitHub Actions setup for Argus Engine

This overlay adds the first CI/CD step for `derekdperez/Argus-Engine`.

## Files included

- `.github/workflows/ci.yml`
  - Builds the .NET solution.
  - Runs unit tests through `./test.sh unit`.
  - Builds all Docker service images without pushing.
  - Uses Docker Buildx GitHub Actions cache per service.

- `.github/workflows/publish-ecr.yml`
  - Runs on pushes to `main` and manual dispatch.
  - Assumes `arn:aws:iam::415144299522:role/ArgusEngineGitHubActionsRole` using GitHub OIDC.
  - Ensures ECR repositories exist.
  - Builds and pushes all images to ECR in parallel.
  - Publishes both immutable `${{ github.sha }}` tags and `latest` tags.

- `deploy/aws/build-push-ecr.sh`
  - Reformatted for maintainability.
  - Keeps the existing EC2/local manual image publish path working.
  - Fixes `worker-enum` to use `ArgusEngine.Workers.Enumeration`.

- `deploy/aws/github-actions/setup-oidc-role.sh`
  - Creates/updates the GitHub Actions OIDC provider and IAM role in account `415144299522`.
  - Grants only the ECR permissions needed by this first publish workflow.

## Apply this overlay

From the repository root:

```bash
unzip argus-engine-github-actions-setup.zip -d .
chmod +x deploy/aws/build-push-ecr.sh
chmod +x deploy/aws/github-actions/setup-oidc-role.sh
```

Create or update the AWS IAM role from your EC2 machine or any shell with enough IAM permissions:

```bash
AWS_REGION=us-east-1 AWS_ACCOUNT_ID=415144299522 bash deploy/aws/github-actions/setup-oidc-role.sh
```

If your production ECR region is not `us-east-1`, use that region instead.

## Optional GitHub variables

The workflow defaults are:

```text
AWS_REGION=us-east-1
ECR_PREFIX=argus-v2
```

To override them:

GitHub repo → Settings → Secrets and variables → Actions → Variables

Add:

```text
AWS_REGION=<your region>
ECR_PREFIX=<your ECR prefix>
```

No long-lived AWS access keys are required.

## Commit and push

```bash
git checkout -b add-github-actions
git add .github/workflows/ci.yml .github/workflows/publish-ecr.yml deploy/aws/build-push-ecr.sh deploy/aws/github-actions
git commit -m "Add GitHub Actions CI and ECR publishing"
git push -u origin add-github-actions
```

Open a pull request. The `CI` workflow should run on the pull request. After merge to `main`, `Build and Push Images to ECR` will publish images.

## Manual publish

From GitHub Actions → `Build and Push Images to ECR` → Run workflow.

To publish all services, keep:

```text
all
```

To publish one or more services:

```text
command-center worker-spider worker-enum
```
