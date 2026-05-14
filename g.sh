#!/usr/bin/env bash

set -e

COMMIT_MESSAGE="${1:-Auto commit: update changes}"

# Make sure we are inside a Git repo
if ! git rev-parse --is-inside-work-tree > /dev/null 2>&1; then
  echo "Error: Not inside a Git repository."
  exit 1
fi

# Check for changes, including untracked files
if [[ -z "$(git status --porcelain)" ]]; then
  echo "No changes to commit."
  exit 0
fi

echo "Changes detected."

git add -A
git commit -m "$COMMIT_MESSAGE"

CURRENT_BRANCH="$(git branch --show-current)"

if [[ -z "$CURRENT_BRANCH" ]]; then
  echo "Error: Could not determine current branch."
  exit 1
fi

echo "Pushing to origin/$CURRENT_BRANCH..."
git push origin "$CURRENT_BRANCH"

echo "Done."