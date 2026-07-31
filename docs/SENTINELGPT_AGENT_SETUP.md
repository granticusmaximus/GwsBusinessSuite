# SentinelGPT agent setup

SentinelGPT uses a local Ollama model for reasoning. The GWS server owns every data,
internet, and model-management operation; the model never receives database credentials,
connector tokens, password hashes, protected automation credentials, or unrestricted
network/database access.

## SentinelGPT behavioral profile

Docker creates the version-controlled `sentinelgpt` Ollama profile from
`ollama/SentinelGPT.Modelfile` during Ollama startup. It is based on `llama3.2`, uses a
low temperature and a 16K context window, and instructs the model to:

- prioritize correctness over agreement and respectfully challenge faulty assumptions;
- separate confirmed facts, inferences, recommendations, and unknowns;
- verify current/version-sensitive claims from supplied current evidence;
- prefer official Microsoft developer documentation for .NET ecosystem questions;
- never claim an application action succeeded without a confirmed server-side result.

For local Ollama outside Docker, create or refresh the same profile from the repository
root:

```zsh
ollama pull llama3.2
ollama create sentinelgpt -f ollama/SentinelGPT.Modelfile
ollama run sentinelgpt
```

The application uses `sentinelgpt` by default when no model override has been selected.
Recreating the profile updates its behavior without altering the underlying `llama3.2`
weights.

## Qwen and DeepSeek teacher panel

SentinelGPT uses the installed specialist models without pretending their weights have
been merged:

- **Fast** mode is the default and sends the grounded request directly to SentinelGPT;
- **Deep** mode is an explicit composer control that consults `qwen2.5-coder` and
  `deepseek-r1` before SentinelGPT writes the final answer;
- Qwen acts as the .NET/C#/Blazor engineering reviewer;
- DeepSeek audits premises, missing evidence, counterexamples, and reasoning;
- SentinelGPT receives both opinions as explicitly untrusted advisory material and produces
  the final response using verified GWS context and cited documentation;
- specialist calls never run merely because a prompt contains technical keywords.

The teacher responses are not factual sources and are not automatically saved as memory.
Use the thumbs-up control below a SentinelGPT response to approve it as reusable learning
memory. Thumbs-down rejects or removes it from the eligible memory set. Future related
questions retrieve up to four relevant human-approved lessons. Model-management responses
are never eligible for learning memory.

At startup, GWS creates an active **SentinelGPT Teacher Panel** workflow if one does not
already exist. Open **Automation**, select that workflow, and run it with input shaped like:

```json
{
  "prompt": "Review this proposed Blazor architecture and correct my assumptions."
}
```

The visible workflow is:

```text
Manual Trigger
  ├─ Qwen engineering review ─┐
  └─ DeepSeek reasoning review ─┤
                               Merge
                                 ↓
                      SentinelGPT final synthesis
                                 ↓
                      Approve as learning memory
                         ├─ approved → Save approved lesson
                         └─ rejected → Discard rejected lesson
```

Inspect the SentinelGPT synthesis in the execution evidence before approving it. Approval
is the security and quality boundary: installed models cannot silently teach persistent
behavior or turn an unsupported claim into a remembered fact.

## Capabilities

- Every normal SentinelGPT question receives a sanitized live GWS Business Suite overview
  plus relevant records from Sentinel, publishing, CRM, automation, container health,
  intelligence, podcasts, affiliate operations, page generation, Notion sync status, and
  live-show modules.
- The sanitized suite overview and term-matched module context use a 20-second in-memory
  cache. Cache keys contain only normalized search terms, cached records exclude protected
  and unnecessary fields, and distinct term sets remain isolated.
- Conversation history and grounding context are bounded before they reach Ollama so long
  chats do not repeatedly re-evaluate an ever-growing prompt.
- Local model generation is coordinated inside the web process to match the production
  one-request Ollama limit. Interactive requests are selected before queued scheduled
  news, CJ suggestion, or automation work. A background generation already running is
  allowed to finish; the chat takes the next available model slot.
- The **Web** control in the composer optionally adds current Ollama web-search results.
  External results are labeled and linked separately from internal GWS sources.
  When Web is on, the current prompt is sent to Ollama's web-search service; do not put
  secrets or private credentials in an internet-enabled prompt.
- For .NET, C#, ASP.NET Core, Blazor, EF Core, NuGet, MSBuild, Visual Studio, and MAUI
  questions with **Web** enabled, SentinelGPT performs an additional search scoped to
  Microsoft Learn. Only HTTPS results from Microsoft Learn, dotnet.microsoft.com, or an
  official `github.com/dotnet` repository are accepted into the official-documentation
  context. This on-demand retrieval is preferred to embedding a stale copy of every
  Microsoft document in the model.
- Model operations are available from the same chat input:

  ```text
  /models
  /model use qwen3:8b
  /model install qwen3:8b
  /model update qwen3:8b
  update all models
  ```

  Listing and switching are immediate. Installing, updating, and updating all models
  require an explicit confirmation because pulls can consume substantial disk, network,
  memory, and CPU resources.

## Configure internet access locally

The web-search key is a server secret. Do not paste it into SentinelGPT, `appsettings.json`,
source control, screenshots, logs, or support messages.

From the repository root, save it with ASP.NET Core Secret Manager:

```zsh
read -s "OLLAMA_WEB_KEY?Paste the Ollama web-search API key: "
echo
dotnet user-secrets set "OllamaWeb:ApiKey" "$OLLAMA_WEB_KEY" \
  --project src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj
unset OLLAMA_WEB_KEY
```

Restart the web application. Open `/admin/sentinel-gpt`; the header should say
**Web ready**. Turn **Web on** in the composer and ask a current-information question.
The completed response should include web sources with globe icons.

To remove the local secret:

```zsh
dotnet user-secrets remove "OllamaWeb:ApiKey" \
  --project src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj
```

## Configure internet access in Docker/production

The repository's `.env` file is gitignored. On the production host, create or edit it
beside `docker-compose.yml`:

```text
OLLAMA_WEB_API_KEY=replace-with-the-real-key
```

Then recreate only the web application container:

```zsh
docker compose up -d --build --no-deps gwssuite
docker compose ps
docker compose logs --tail=100 gwssuite
```

Do not put the key directly in `docker-compose.yml`. For a larger deployment, replace the
`.env` value with the hosting platform's managed secret store while retaining the
`OllamaWeb__ApiKey` configuration name inside the container.

## Verify the complete flow

1. Open SentinelGPT and confirm **Local · Private** and **Web ready**. When Web is
   enabled, the status changes to **Local model · Web research**.
2. Enter `/models` and verify the installed-model list.
3. Enter `/model update <installed-model>` and verify that a confirmation card appears.
4. Cancel once to verify that no pull starts.
5. Repeat and confirm; wait for the success response.
6. Turn **Web on** and ask:
   `What is the latest stable ASP.NET Core release? Cite current official sources.`
7. Expand **Sources** and verify that external links use HTTPS and open separately.
8. Turn **Web off** and ask about a GWS record or module. Confirm that only internal GWS
   and Sentinel sources appear.
9. Ask about a protected secret. SentinelGPT should refuse to expose it because those
   fields are never placed in model context.

## Response recovery

Normal chat generation is owned by the server rather than the interactive Blazor page.
Refreshing the page, closing the tab, or temporarily losing the SignalR connection stops
only that browser's live polling; the model request continues under a server-owned scope.
When the same user returns while it is still running, SentinelGPT reopens the active
conversation and resumes the streamed progress. On completion, the existing
`SentinelAiRun` persistence remains the durable conversation history.

Production logs separately report request-preparation time, model-slot queue time, model
load time, first-token time, prompt evaluation, and output tokens per second. Together,
these show whether a slow response came from database grounding, queued background work,
a cold model load, a large prompt, or CPU/GPU generation speed.

One active chat response is allowed per user. This prevents duplicate model work when a
user refreshes or double-submits during recovery. A full application/container restart
still interrupts an in-flight model request, which must be sent again after the app is
ready.

While a normal chat response is running, the send control becomes **Stop generating**.
Stopping cancels the server-owned generation token all the way through the Ollama HTTP
stream, releases the active-response slot, and returns the composer to an idle state. Only
the user who owns a generation can inspect or stop it.

## Current safety boundary

SentinelGPT's broad GWS access is read-only. Dedicated GWS workflows can still create
drafts or perform their existing reviewed actions, but general chat cannot publish,
delete, deploy, run automations, resync connectors, or edit business records. Those
capabilities should be added individually with server-side authorization, an action
preview, explicit confirmation, idempotency, and an audit record.

The live application context describes persisted GWS data and module state. It does not
automatically expose the repository's source tree in production; source-code retrieval
would require a separate sanitized code index and is intentionally outside this secret-
safe data gateway.
