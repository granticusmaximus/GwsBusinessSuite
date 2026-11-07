# GWS Business Suite

**Read `AGENTS.md` at the repository root before any change.** It's the single rulebook for every
assistant in this repo: architecture, conventions, known traps, and the environment. If anything
here seems to disagree with it, `AGENTS.md` wins.

These rules are repeated here because breaking them has broken production:

1. **Every push to `main` deploys to production.** Don't push, open PRs or merge unless Grant
   gives permission in the current session.
2. **Start from current code.** `git fetch`, then confirm `git rev-list --count HEAD..origin/main`
   is `0` before working. An old worktree or branch must merge `origin/main` first. A worktree
   reused from May 2026 led to a PR that broke production on 02-11-26.
3. **Look before you build.** Search the code for an existing implementation and extend it. Don't
   add a parallel service.
4. **Before you call work done, run `./scripts/verify-release.sh`** (the full CI build and tests,
   7-8 minutes). `dotnet build` and `dotnet test` alone aren't enough. Report the result, and
   keep zero warnings: they're errors here (`TreatWarningsAsErrors`), so one fails the build.
5. **Update the matching `docs/*_USER_GUIDE.md`** for user-visible changes, and track multi-part
   work in `.claude/plans/`.
