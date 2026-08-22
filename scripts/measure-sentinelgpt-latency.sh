#!/usr/bin/env bash

set -euo pipefail

ollama_base_url="${OLLAMA_BASE_URL:-http://ollama:11434}"
model="${SENTINELGPT_MODEL:-sentinelgpt}"
sample_count="${SENTINELGPT_LATENCY_SAMPLE_COUNT:-3}"
max_output_tokens="${SENTINELGPT_LATENCY_MAX_OUTPUT_TOKENS:-64}"
first_token_objective_ms="${SENTINELGPT_FIRST_TOKEN_OBJECTIVE_MS:-30000}"
total_objective_ms="${SENTINELGPT_TOTAL_OBJECTIVE_MS:-120000}"

for value_name in sample_count max_output_tokens first_token_objective_ms total_objective_ms; do
  value="${!value_name}"
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
    echo "$value_name must be a positive integer." >&2
    exit 2
  fi
done

for dependency in curl jq; do
  if ! command -v "$dependency" >/dev/null 2>&1; then
    echo "$dependency is required for the SentinelGPT latency probe." >&2
    exit 2
  fi
done

probe_tmp_dir="$(mktemp -d)"
cleanup() {
  if [[ -n "${probe_tmp_dir:-}" && -d "$probe_tmp_dir" ]]; then
    rm -rf -- "$probe_tmp_dir"
  fi
}
trap cleanup EXIT

payload="$(jq -nc \
  --arg model "$model" \
  --arg prompt "Reply with one short sentence confirming that the service is ready." \
  --argjson max_output_tokens "$max_output_tokens" \
  '{model:$model, prompt:$prompt, stream:true, keep_alive:"30m", options:{num_predict:$max_output_tokens}}')"

warmup_payload="$(jq -nc \
  --arg model "$model" \
  '{model:$model, prompt:"", stream:false, keep_alive:"30m", options:{num_predict:1}}')"

curl --fail-with-body --silent --show-error \
  --max-time 300 \
  --header "Content-Type: application/json" \
  --data "$warmup_payload" \
  "$ollama_base_url/api/generate" >/dev/null

now_ms() {
  date +%s%3N
}

sample_first_token_ms=0
sample_total_ms=0

run_sample() {
  local sample_number="$1"
  local fifo_path="$probe_tmp_dir/sample-$sample_number.ndjson"
  local curl_pid
  local started_ms
  local first_token_at_ms=0
  local finished_ms
  local line
  local fragment
  local done_seen=false

  mkfifo "$fifo_path"
  started_ms="$(now_ms)"
  curl --fail-with-body --silent --show-error --no-buffer \
    --max-time 300 \
    --header "Content-Type: application/json" \
    --data "$payload" \
    "$ollama_base_url/api/generate" >"$fifo_path" &
  curl_pid=$!

  while IFS= read -r line; do
    if [[ "$first_token_at_ms" -eq 0 ]]; then
      fragment="$(jq -r '.response // empty' <<<"$line")"
      if [[ -n "$fragment" ]]; then
        first_token_at_ms="$(now_ms)"
      fi
    fi

    if [[ "$(jq -r '.done // false' <<<"$line")" == "true" ]]; then
      done_seen=true
    fi
  done <"$fifo_path"

  if ! wait "$curl_pid"; then
    echo "SentinelGPT latency sample $sample_number failed before completion." >&2
    exit 1
  fi
  rm -f -- "$fifo_path"
  finished_ms="$(now_ms)"

  if [[ "$first_token_at_ms" -eq 0 || "$done_seen" != "true" ]]; then
    echo "SentinelGPT latency sample $sample_number returned no complete streamed response." >&2
    exit 1
  fi

  sample_first_token_ms=$((first_token_at_ms - started_ms))
  sample_total_ms=$((finished_ms - started_ms))
  echo "Sample $sample_number: first token ${sample_first_token_ms} ms; total ${sample_total_ms} ms." >&2
}

max_first_token_ms=0
max_total_ms=0
sum_first_token_ms=0
sum_total_ms=0

for ((sample_number = 1; sample_number <= sample_count; sample_number++)); do
  run_sample "$sample_number"
  ((sample_first_token_ms > max_first_token_ms)) && max_first_token_ms="$sample_first_token_ms"
  ((sample_total_ms > max_total_ms)) && max_total_ms="$sample_total_ms"
  sum_first_token_ms=$((sum_first_token_ms + sample_first_token_ms))
  sum_total_ms=$((sum_total_ms + sample_total_ms))
done

average_first_token_ms=$((sum_first_token_ms / sample_count))
average_total_ms=$((sum_total_ms / sample_count))
objectives_met=true
if ((max_first_token_ms > first_token_objective_ms || max_total_ms > total_objective_ms)); then
  objectives_met=false
fi

jq -nc \
  --arg model "$model" \
  --argjson sampleCount "$sample_count" \
  --argjson maxOutputTokens "$max_output_tokens" \
  --argjson averageFirstTokenMilliseconds "$average_first_token_ms" \
  --argjson maximumFirstTokenMilliseconds "$max_first_token_ms" \
  --argjson firstTokenObjectiveMilliseconds "$first_token_objective_ms" \
  --argjson averageTotalMilliseconds "$average_total_ms" \
  --argjson maximumTotalMilliseconds "$max_total_ms" \
  --argjson totalObjectiveMilliseconds "$total_objective_ms" \
  --argjson objectivesMet "$objectives_met" \
  '{Model:$model, SampleCount:$sampleCount, MaxOutputTokens:$maxOutputTokens,
    AverageFirstTokenMilliseconds:$averageFirstTokenMilliseconds,
    MaximumFirstTokenMilliseconds:$maximumFirstTokenMilliseconds,
    FirstTokenObjectiveMilliseconds:$firstTokenObjectiveMilliseconds,
    AverageTotalMilliseconds:$averageTotalMilliseconds,
    MaximumTotalMilliseconds:$maximumTotalMilliseconds,
    TotalObjectiveMilliseconds:$totalObjectiveMilliseconds,
    ObjectivesMet:$objectivesMet}'

[[ "$objectives_met" == "true" ]]
