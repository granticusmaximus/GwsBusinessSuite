# Working in this repo

## Verify before you consider work done

This repo auto-publishes working-tree changes to `origin/main`, which triggers a real
production deploy — there is no draft state. Treat every edit as already shipped, not staged
for review.

Before ending a turn that changed code, run:

```
./scripts/verify-release.sh
```

It runs the exact build + full test suite (including Playwright browser tests) that CI runs.
A `pre-push` git hook also runs it automatically (see `.githooks/pre-push` — enabled per-clone
via `git config core.hooksPath .githooks`), but don't rely on the hook alone: by the time a
push happens here, the code may already be live. Catch it before that point.

This exists because of a real incident: a constructor signature change broke an existing test
file. It wasn't run locally before the change shipped, and only surfaced when CI failed on the
already-pushed commit. The tool to catch it locally already existed; it just wasn't used.

## Multi-part or risky changes

For anything with multiple sequenced pieces (a phased plan, a multi-file feature), finish and
verify the *whole* unit before stopping — don't leave it half-applied. If the user says not to
push until a phase is complete, that means don't stop mid-phase for anything other than being
genuinely blocked.
