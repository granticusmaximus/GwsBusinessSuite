# SentinelCLI — User Guide

SentinelCLI is the local terminal coding agent shipped with the GWS macOS client source.
It connects only to the Ollama server on your own Mac and can inspect one repository, a parent
directory containing several repositories, or any other directory you explicitly select. It can
analyze code, propose file edits, create files, and run bounded build/test commands.

It is intentionally separate from the hosted SentinelGPT page. The hosted assistant understands
GWS records; this CLI understands files in the local directory you give it. Neither surface sends
repository content to the other.

## Contents

1. [Prerequisites and installation](#prerequisites-and-installation)
2. [Synchronizing the GWS Ollama models](#synchronizing-the-gws-ollama-models)
3. [Running in one repository](#running-in-one-repository)
4. [Running across a directory of repositories](#running-across-a-directory-of-repositories)
5. [Interactive and one-shot use](#interactive-and-one-shot-use)
6. [Planning mode, agents, and skills](#planning-mode-agents-and-skills)
7. [Resuming sessions and running a fleet](#resuming-sessions-and-running-a-fleet)
8. [Analysis, edits, and validation](#analysis-edits-and-validation)
9. [Safety boundaries](#safety-boundaries)
10. [Diagnostics and updates](#diagnostics-and-updates)
11. [Known limitations](#known-limitations)

## Prerequisites and installation

Install and start the official [Ollama macOS application](https://docs.ollama.com/macos) first.
Ollama serves its local API on `http://127.0.0.1:11434`; SentinelCLI refuses non-loopback
Ollama URLs so a typo cannot send source to another host.

From the GWS Business Suite repository, install a self-contained CLI for the current Mac user:

```zsh
./scripts/install-sentinelcli.sh
```

The installer publishes the executable for the Mac's current architecture under
`~/.local/share/gws/sentinelcli` and links `sentinelcli` into `~/.local/bin`. If that directory is
not already in `PATH`, add this to `~/.zprofile`, then open a new Terminal window:

```zsh
export PATH="$HOME/.local/bin:$PATH"
```

No `sudo` access is required. Run `sentinelcli help` to confirm the command is available. On the
first renamed installation, the installer copies any existing sessions from
`~/.local/share/gws/sentinelgpt/sessions` and skills from `~/.config/sentinelgpt/skills` into the
new SentinelCLI locations. It leaves the original files intact.

## Synchronizing the GWS Ollama models

The canonical model list lives in `ollama/required-models.txt` and is used by both production's
Ollama container and this CLI. That prevents the Mac and server bootstrap lists from drifting.

| Model | GWS purpose |
| --- | --- |
| `llama3.2` | Base weights for the customized `sentinelgpt` profile |
| `qwen2.5-coder` | Default CLI coding model and Deep-mode engineering adviser |
| `deepseek-r1` | Deep-mode reasoning adviser |
| `embeddinggemma` | Semantic-search embeddings |
| `sentinelgpt` | GWS behavioral profile rebuilt locally from `ollama/SentinelGPT.Modelfile` |

Install or refresh all of them with:

```zsh
sentinelcli models sync
```

The CLI shows the exact model set and asks before starting because first-time downloads consume
several gigabytes. To combine CLI installation and model synchronization:

```zsh
./scripts/install-sentinelcli.sh --sync-models
```

Run `sentinelcli doctor` afterward. **Canonical GWS models: synchronized** means every required
base model and the derived `sentinelgpt` profile are available under the exact names GWS uses.

## Running in one repository

Change into a repository and start an interactive session:

```zsh
cd ~/Development/MyRepo
sentinelcli
```

Or select a repository without changing directories:

```zsh
sentinelcli -C ~/Development/MyRepo "Find and fix the parser regression, then run its tests"
```

`-C` and `--repo` are equivalent. The selected directory becomes a hard filesystem boundary;
the agent's file tools cannot resolve `..`, an absolute path, or a symbolic link outside it.

## Running across a directory of repositories

Select their parent directory when a request may cross more than one repository:

```zsh
cd ~/Development
sentinelcli "Find which repo owns the customer import endpoint and analyze its validation"
```

SentinelCLI reports the repositories it detects immediately after startup. It still receives no
access outside `~/Development` in this example. For a narrowly scoped change, prefer `-C` with
the specific repository so unrelated repositories never enter the tool surface.

## Interactive and one-shot use

Run `sentinelcli` without a prompt for a continuing conversation. Follow-up questions retain the
current session's tool results and model context.

- `/help` lists every session command.
- `/models` lists the Ollama models already installed locally (same data as
  `sentinelcli models list`, without leaving the session).
- `/availablemodels` shows a curated starting list of free Ollama models — marking which are
  already installed — and prompts for a number (or any model name, including ones not on the
  list) to download. Downloads still go through the same confirmation prompt as everything else.
- `/clear` starts a fresh conversation while keeping the same directory and model.
- `/quit` or `/exit` ends the session.
- `Control-C` cancels the active local model request — you're returned to the prompt, the session
  keeps going.

Put a prompt after the command for a single request suitable for shell history or scripts:

```zsh
sentinelcli --read-only "Explain the architecture and identify the three highest correctness risks"
```

The default model is `qwen2.5-coder`. Use another installed local model for one session with
`--model`, for example `sentinelcli --model sentinelgpt`.

## Planning mode, agents, and skills

- `/plan` switches to read-only planning: mutation and command tools are hidden from the model
  entirely, and it's instructed to produce a numbered plan instead of attempting edits. `/act`
  switches back. If you started with `--read-only`, `/act` says so rather than pretending edits
  are now possible.
- `/agent` lists the built-in personas and shows which one is active; `/agent <name>` switches for
  the rest of the session (`coder` — the default, `reviewer`, `test-writer`, `docs-writer`). A
  persona only shapes tone and priorities in the system prompt — unlike `/plan`, it doesn't
  actually remove any tools. Combine `/agent reviewer` with `/plan` (or `--read-only`) for a
  persona *and* an enforced guarantee that nothing gets edited.
- `/skills` lists the skills discovered under `~/.config/sentinelcli/skills/` — any `.md` file you
  put there becomes a skill named after its filename. `/skills <name> <prompt>` applies that
  file's instructions to a single request (not a standing change like `/agent`/`/plan`). There's
  no built-in skill; add your own, for example a `commit-messages.md` describing how you like
  commit messages phrased.

## Resuming sessions and running a fleet

Every turn is saved automatically to `~/.local/share/gws/sentinelcli/sessions/`, one JSON file per
session, scoped to the workspace directory (`-C`/`--repo`) it was started in. `/resume` lists
saved sessions for the *current* workspace (most recent first, with a preview of the last
question asked) and continues the one you pick — later turns extend that same file rather than
starting a new one. `/clear` always starts a fresh file, so clearing never overwrites a session you
might still want to resume later. There's no locking: running two terminals against the same saved
session at once means the last one to save wins, silently — fine for how this tool is normally
used, just worth knowing.

`/fleet <model,model,...> <prompt>` runs the same request across several installed models at once
and prints each one's answer, labeled, for comparison:

```
sentinelcli> /fleet llama3.2,deepseek-r1,qwen2.5-coder Explain what this function does and flag any bug

=== llama3.2 ===
...
=== deepseek-r1 ===
...
=== qwen2.5-coder ===
...
```

Fleet always runs read-only, regardless of `/plan`/`/act` or `--read-only` — there's no way for a
fleet run to propose an edit, by design, since several models confirming edits against the same
files at once isn't something a single terminal can sensibly present. The model list must be
explicit (no "all installed models" default); on constrained hardware, Ollama's own model-loading
limits may serialize the requests rather than truly overlap them, which isn't a bug in this tool.

## Analysis, edits, and validation

The model receives explicit tools rather than unrestricted filesystem or shell access:

- `list_files`, `read_file`, and `search_text` inspect bounded, non-secret text files.
- `replace_in_file` proposes an exact replacement in an existing file.
- `write_file` proposes creating or deliberately replacing one text file.
- `run_command` proposes an allowlisted build, test, syntax, or read-only Git command. Arguments
  are passed directly to the executable, never through a free-form shell string.

Every edit and command prints a preview and asks **Apply? [y/N]**. A declined operation is returned
to the model as declined; the model cannot claim it happened. `--read-only` removes all mutation
and command tools. `--yes` auto-approves in-scope proposals and is intended only for a repository
and prompt you have already reviewed.

The agent cannot commit, push, publish, deploy, run `git reset`, or run another destructive Git
operation. Grant remains the only publisher of GWS code.

## Safety boundaries

- Known credentials and secret locations (`.env`, private keys, `.ssh`, `.aws`, `.azure`, and
  similar files) are blocked even when they are inside the selected directory.
- `.git`, dependencies, build output, caches, binary files, and files above the size limit are
  excluded from broad listing/search.
- Existing symbolic links are resolved before access and rejected if their target leaves the
  workspace.
- Repository content is treated as untrusted. An instruction hidden in a source file cannot
  grant more tool access, reveal secrets, or override confirmation.
- The Ollama URL must resolve to loopback. Repository prompts and tool results remain between the
  CLI process and the local Ollama service.

## Diagnostics and updates

Useful commands:

```zsh
sentinelcli doctor
sentinelcli models list
sentinelcli models sync
sentinelcli help
```

Run `./scripts/install-sentinelcli.sh` again after updating the GWS repository. The installer
re-publishes the current source and refreshes the user-level link without changing repositories.

If `doctor` says Ollama is unavailable, start the Ollama macOS app. If it reports missing models,
run `sentinelcli models sync`. If the command itself is not found, confirm `~/.local/bin` is in
`PATH`.

## Known limitations

- Local models can generate and edit useful code, but they are smaller than frontier hosted
  coding models and may need more explicit prompts or corrections. Review every proposed edit.
- The CLI has no browser or internet research tool. It operates on local repository evidence and
  the model's existing weights.
- It does not index repositories permanently; each session inspects files on demand.
- It edits text files only and deliberately avoids package publishing, Git history mutation,
  remote repository operations, and deployments.
- A one-shot command with redirected standard input cannot display an interactive confirmation;
  edits are declined unless the user explicitly supplied `--yes`.
- Saved sessions have no locking between concurrent terminals against the same workspace, and are
  never pruned automatically (only the *displayed* `/resume` list is capped, at the 20 most
  recent) — delete old files under `~/.local/share/gws/sentinelcli/sessions/` yourself if that
  ever matters to you.
- `/skills` files are plain instructions with no format beyond "this whole file is the prompt
  addition" — there's no frontmatter, metadata, or validation.
