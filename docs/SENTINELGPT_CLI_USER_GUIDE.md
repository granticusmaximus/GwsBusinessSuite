# SentinelGPT Code CLI — User Guide

SentinelGPT Code is the local terminal coding agent shipped with the GWS macOS client source.
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
6. [Analysis, edits, and validation](#analysis-edits-and-validation)
7. [Safety boundaries](#safety-boundaries)
8. [Diagnostics and updates](#diagnostics-and-updates)
9. [Known limitations](#known-limitations)

## Prerequisites and installation

Install and start the official [Ollama macOS application](https://docs.ollama.com/macos) first.
Ollama serves its local API on `http://127.0.0.1:11434`; SentinelGPT Code refuses non-loopback
Ollama URLs so a typo cannot send source to another host.

From the GWS Business Suite repository, install a self-contained CLI for the current Mac user:

```zsh
./scripts/install-sentinelgpt-cli.sh
```

The installer publishes the executable for the Mac's current architecture under
`~/.local/share/gws/sentinelgpt` and links `sentinelgpt` into `~/.local/bin`. If that directory is
not already in `PATH`, add this to `~/.zprofile`, then open a new Terminal window:

```zsh
export PATH="$HOME/.local/bin:$PATH"
```

No `sudo` access is required. Run `sentinelgpt help` to confirm the command is available.

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
sentinelgpt models sync
```

The CLI shows the exact model set and asks before starting because first-time downloads consume
several gigabytes. To combine CLI installation and model synchronization:

```zsh
./scripts/install-sentinelgpt-cli.sh --sync-models
```

Run `sentinelgpt doctor` afterward. **Canonical GWS models: synchronized** means every required
base model and the derived `sentinelgpt` profile are available under the exact names GWS uses.

## Running in one repository

Change into a repository and start an interactive session:

```zsh
cd ~/Development/MyRepo
sentinelgpt
```

Or select a repository without changing directories:

```zsh
sentinelgpt -C ~/Development/MyRepo "Find and fix the parser regression, then run its tests"
```

`-C` and `--repo` are equivalent. The selected directory becomes a hard filesystem boundary;
the agent's file tools cannot resolve `..`, an absolute path, or a symbolic link outside it.

## Running across a directory of repositories

Select their parent directory when a request may cross more than one repository:

```zsh
cd ~/Development
sentinelgpt "Find which repo owns the customer import endpoint and analyze its validation"
```

SentinelGPT reports the repositories it detects immediately after startup. It still receives no
access outside `~/Development` in this example. For a narrowly scoped change, prefer `-C` with
the specific repository so unrelated repositories never enter the tool surface.

## Interactive and one-shot use

Run `sentinelgpt` without a prompt for a continuing conversation. Follow-up questions retain the
current session's tool results and model context.

- `/clear` starts a fresh conversation while keeping the same directory and model.
- `/quit` or `/exit` ends the session.
- `Control-C` cancels the active local model request.

Put a prompt after the command for a single request suitable for shell history or scripts:

```zsh
sentinelgpt --read-only "Explain the architecture and identify the three highest correctness risks"
```

The default model is `qwen2.5-coder`. Use another installed local model for one session with
`--model`, for example `sentinelgpt --model sentinelgpt`.

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
sentinelgpt doctor
sentinelgpt models list
sentinelgpt models sync
sentinelgpt help
```

Run `./scripts/install-sentinelgpt-cli.sh` again after updating the GWS repository. The installer
re-publishes the current source and refreshes the user-level link without changing repositories.

If `doctor` says Ollama is unavailable, start the Ollama macOS app. If it reports missing models,
run `sentinelgpt models sync`. If the command itself is not found, confirm `~/.local/bin` is in
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
