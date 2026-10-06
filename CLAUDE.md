# Working in this repo

## No push authorization

Claude has no authorization of any form to push code to any of Grant's GitHub repos. Claude
has read access only. Never run `git push` (including `--force`), and never push by any other
route (`gh`, API calls, etc.).

The only exception is a git error Grant asks for help with. After the problem is worked out,
Claude may force push, but only when Grant explicitly says to force push for that specific case.
That instruction covers that one case and does not carry over to later pushes.

## Verify before you consider work done

Nothing here commits or pushes automatically. Grant commits and pushes to `main` manually,
sometimes outside the session and sometimes soon after an edit lands. A push to `origin/main`
triggers a real production deploy, and there is no draft state. Treat every working-tree edit
as something that could ship at any moment, not as staged for review.

Before ending a turn that changed code, run:

```
./scripts/verify-release.sh
```

It runs the exact build + full test suite (including Playwright browser tests) that CI runs.
A `pre-push` git hook also runs it automatically (see `.githooks/pre-push` — enabled per-clone
via `git config core.hooksPath .githooks`), but don't rely on the hook alone. It only runs
when Grant pushes, which may be right after your edit and without another look at it. Catch
problems before that point.

This exists because of a real incident: a constructor signature change broke an existing test
file. It wasn't run locally before the change shipped, and only surfaced when CI failed on the
already-pushed commit. The tool to catch it locally already existed; it just wasn't used.

## Multi-part or risky changes

For anything with multiple sequenced pieces (a phased plan, a multi-file feature), finish and
verify the *whole* unit before stopping — don't leave it half-applied. If the user says not to
push until a phase is complete, that means don't stop mid-phase for anything other than being
genuinely blocked.
