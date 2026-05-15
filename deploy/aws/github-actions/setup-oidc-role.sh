#!/usr/bin/env bash
set -euo pipefail

AWS_ACCOUNT_ID="${AWS_ACCOUNT_ID:-415144299522}"
AWS_REGION="${AWS_REGION:-us-east-1}"
GITHUB_REPO="${GITHUB_REPO:-derekdperez/Argus-Engine}"
ROLE_NAME="${ROLE_NAME:-ArgusEngineGitHubActionsRole}"
ECR_PREFIX="${ECR_PREFIX:-argus-v2}"

OIDC_PROVIDER_HOST="token.actions.githubusercontent.com"
OIDC_PROVIDER_URL="https://${OIDC_PROVIDER_HOST}"
OIDC_PROVIDER_ARN="arn:aws:iam::${AWS_ACCOUNT_ID}:oidc-provider/${OIDC_PROVIDER_HOST}"
ROLE_ARN="arn:aws:iam::${AWS_ACCOUNT_ID}:role/${ROLE_NAME}"

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT

echo "Using account: ${AWS_ACCOUNT_ID}"
echo "Using region:  ${AWS_REGION}"
echo "Using repo:    ${GITHUB_REPO}"
echo "Using role:    ${ROLE_NAME}"

if ! aws iam get-open-id-connect-provider   --open-id-connect-provider-arn "$OIDC_PROVIDER_ARN" >/dev/null 2>&1; then
  echo "Creating GitHub Actions OIDC provider..."
  aws iam create-open-id-connect-provider     --url "$OIDC_PROVIDER_URL"     --client-id-list sts.amazonaws.com     --thumbprint-list 6938fd4d98bab03faadb97b34396831e3780aea1 >/dev/null
else
  echo "OIDC provider already exists."
fi

cat > "${tmpdir}/trust-policy.json" <<JSON
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Federated": "${OIDC_PROVIDER_ARN}"
      },
      "Action": "sts:AssumeRoleWithWebIdentity",
      "Condition": {
        "StringEquals": {
          "${OIDC_PROVIDER_HOST}:aud": "sts.amazonaws.com",
          "${OIDC_PROVIDER_HOST}:sub": "repo:${GITHUB_REPO}:ref:refs/heads/main"
        }
      }
    }
  ]
}
JSON

cat > "${tmpdir}/ecr-publish-policy.json" <<JSON
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "EcrAuth",
      "Effect": "Allow",
      "Action": [
        "ecr:GetAuthorizationToken"
      ],
      "Resource": "*"
    },
    {
      "Sid": "CreateAndDiscoverArgusRepositories",
      "Effect": "Allow",
      "Action": [
        "ecr:CreateRepository",
        "ecr:DescribeRepositories"
      ],
      "Resource": "*"
    },
    {
      "Sid": "PushAndReadArgusImages",
      "Effect": "Allow",
      "Action": [
        "ecr:BatchCheckLayerAvailability",
        "ecr:BatchGetImage",
        "ecr:CompleteLayerUpload",
        "ecr:DescribeImages",
        "ecr:GetDownloadUrlForLayer",
        "ecr:InitiateLayerUpload",
        "ecr:ListImages",
        "ecr:PutImage",
        "ecr:UploadLayerPart"
      ],
      "Resource": "arn:aws:ecr:${AWS_REGION}:${AWS_ACCOUNT_ID}:repository/${ECR_PREFIX}/*"
    }
  ]
}
JSON

if aws iam get-role --role-name "$ROLE_NAME" >/dev/null 2>&1; then
  echo "Updating role trust policy..."
  aws iam update-assume-role-policy     --role-name "$ROLE_NAME"     --policy-document "file://${tmpdir}/trust-policy.json" >/dev/null
else
  echo "Creating role..."
  aws iam create-role     --role-name "$ROLE_NAME"     --assume-role-policy-document "file://${tmpdir}/trust-policy.json" >/dev/null
fi

echo "Putting inline ECR publish policy..."
aws iam put-role-policy   --role-name "$ROLE_NAME"   --policy-name ArgusEngineEcrPublish   --policy-document "file://${tmpdir}/ecr-publish-policy.json" >/dev/null

echo ""
echo "Done."
echo "Role ARN: ${ROLE_ARN}"
echo ""
echo "The publish workflow is already configured to assume this role."
echo "Optional GitHub repo variables:"
echo "  AWS_REGION=${AWS_REGION}"
echo "  ECR_PREFIX=${ECR_PREFIX}"
