#!/usr/bin/env bash

set -e

COMMIT_MESSAGE="${1:-Auto commit: update changes}"

get_script_path() {
  cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1
  printf "%s/%s\n" "$(pwd -P)" "$(basename -- "${BASH_SOURCE[0]}")"
}

shell_quote() {
  printf "'%s'" "$(printf "%s" "$1" | sed "s/'/'\\\\''/g")"
}

ensure_g_alias() {
  local script_path
  local shell_name
  local rc_file
  local expected_alias

  script_path="$(get_script_path)"
  shell_name="$(basename "${SHELL:-}")"

  case "$shell_name" in
    bash)
      rc_file="$HOME/.bashrc"
      ;;
    zsh)
      rc_file="$HOME/.zshrc"
      ;;
    *)
      echo "Warning: Unsupported shell '$shell_name'. Please manually add:"
      echo "alias g=$(shell_quote "$script_path")"
      return 0
      ;;
  esac

  expected_alias="alias g=$(shell_quote "$script_path")"

  touch "$rc_file"

  if grep -Eq '^[[:space:]]*alias[[:space:]]+g=' "$rc_file"; then
    if grep -Fxq "$expected_alias" "$rc_file"; then
      echo "Alias check passed: g points to this script."
      return 0
    fi

    echo "Error: alias 'g' already exists in $rc_file, but it does not point to this script."
    echo "Expected:"
    echo "$expected_alias"
    exit 1
  fi

  {
    echo ""
    echo "# Added by git-auto-commit-push.sh"
    echo "$expected_alias"
  } >> "$rc_file"

  echo "Added alias to $rc_file:"
  echo "$expected_alias"
  echo "Run this once to enable it in your current terminal:"
  echo "source $rc_file"
}

ensure_g_alias

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