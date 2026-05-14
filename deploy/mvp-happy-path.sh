#!/bin/bash
set -e

# Argus Engine MVP Happy Path Smoke Test
# This script verifies the end-to-end flow: Target -> Enumeration -> Queue

BASE_URL=${1:-"http://localhost:8080"}
ROOT_DOMAIN="example-$(date +%s).com"

echo "--- 1. Posting new target: $ROOT_DOMAIN ---"
POST_RESPONSE=$(curl -s -X POST "$BASE_URL/api/targets" \
  -H "Content-Type: application/json" \
  -d "{\"rootDomain\": \"$ROOT_DOMAIN\", \"globalMaxDepth\": 2}")

TARGET_ID=$(echo $POST_RESPONSE | jq -r '.targetId')

if [ "$TARGET_ID" == "null" ] || [ -z "$TARGET_ID" ]; then
  echo "Error: Failed to create target"
  echo "$POST_RESPONSE"
  exit 1
fi

echo "Created target ID: $TARGET_ID"

echo "--- 2. Waiting for target to appear in summaries ---"
FOUND=false
for i in {1..10}; do
  SUMMARIES=$(curl -s "$BASE_URL/api/targets")
  if echo "$SUMMARIES" | jq -e ".[] | select(.id == \"$TARGET_ID\")" > /dev/null; then
    FOUND=true
    break
  fi
  sleep 2
done

if [ "$FOUND" = false ]; then
  echo "Error: Target did not appear in /api/targets within 20 seconds"
  exit 1
fi

echo "Target verified in summary list."

echo "--- 3. Monitoring for discovery progress (Assets or Queue) ---"
echo "Waiting up to 60 seconds for workers to start processing..."

SUCCESS=false
for i in {1..30}; do
  TARGET_STATUS=$(curl -s "$BASE_URL/api/targets" | jq ".[] | select(.id == \"$TARGET_ID\")")
  ASSET_COUNT=$(echo "$TARGET_STATUS" | jq -r '.confirmedAssetCount // 0')
  QUEUE_COUNT=$(echo "$TARGET_STATUS" | jq -r '.queuedAssetCount // 0')
  
  echo "  [$(date +%T)] Assets: $ASSET_COUNT, Queued: $QUEUE_COUNT"
  
  if [ "$ASSET_COUNT" -gt 0 ] || [ "$QUEUE_COUNT" -gt 0 ]; then
    SUCCESS=true
    break
  fi
  sleep 2
done

if [ "$SUCCESS" = true ]; then
  echo "SUCCESS: Discovery work detected for target $ROOT_DOMAIN."
else
  echo "FAILURE: No discovery progress detected within 60 seconds."
  echo "Check worker logs: docker compose logs worker-enum worker-spider"
  exit 1
fi

echo "--- MVP Happy Path Smoke Test Passed ---"
