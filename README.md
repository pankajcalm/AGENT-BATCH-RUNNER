# AgentBatchRunner

AgentBatchRunner is a local .NET 8 console tool that runs YAML-defined coding prompts one at a time through Claude Code, Codex, or a safe dry-run adapter. It records Git checkpoints, verification output, retry attempts, per-task state, and final Markdown/JSON reports under `.agentbatchrunner/runs/`.

## Build

```powershell
dotnet build AgentBatchRunner.sln
```

## Validate a Prompt File

```powershell
dotnet run --project src\AgentBatchRunner -- validate prompts.yaml
```

## Run in Dry-Run Mode

```powershell
dotnet run --project src\AgentBatchRunner -- run prompts.yaml --agent dryrun
```

Dry-run mode does not call Claude or Codex. It logs the prompt that would have been executed, then runs the configured verification commands.

## Launch the Windows GUI

```powershell
dotnet run --project src\AgentBatchRunner.Gui
```

The WPF GUI lets you browse for a `.yaml`/`.yml` prompt file, validate it, run or cancel the batch,
watch prompt status/logs live, inspect latest attempt outputs, and open the final report/run folder.
Its default routing mode is **From YAML (recommended)**, so mixed Claude/Codex files retain each
prompt's configured agent. The explicit `Global override: dryrun`, `Global override: claude`, and
`Global override: codex` choices intentionally replace the agent for every prompt and display a
warning. Loading a different YAML file resets routing to From YAML.

While a run is active, the global selector is disabled. Use **Switch Pending Agent** to queue a
run-local change for prompts that have not started. A queued change does not cancel or alter the
current process. After a rate-limited stop, the same command can continue the stopped task with a
fresh replacement-agent session while retaining the run ID, checkpoint, and prior attempt files.

GUI validation also performs local agent preflight. Run stays disabled until every required agent
executable has been resolved and its version check has passed. The exact executable paths and
versions are shown before execution.

## Agent routing

AgentBatchRunner uses one precedence rule in validation, GUI preview, execution, resume artifacts,
and reports:

1. An explicit run override, such as CLI `--agent codex` or GUI `Global override: codex`.
2. The prompt's `agent` value.
3. The YAML `defaultAgent`.
4. Validation fails if none is available.

Omitting CLI `--agent` means **no global override**. For mixed-agent YAML, run without `--agent`:

```powershell
dotnet run --project src\AgentBatchRunner -- run prompts.yaml
```

Use `--agent` only when replacing all prompt-level routing is deliberate.

Routing fields have distinct meanings in state and reports:

- `ConfiguredAgent`: the prompt's YAML `agent` value.
- `DefaultAgent`: the YAML fallback.
- `RunOverride`: an explicit CLI/GUI global override selected before the run.
- `BaseAgent`: the result of the three-level precedence rule above.
- `EffectiveAgent`: the base agent after any run-local fallback or manual pending switch.
- `AttemptAgent`: the agent actually invoked for one attempt folder.
- `RoutingReason`: `Yaml`, `Default`, `GlobalOverride`, `RateLimitFallback`, or `ManualPendingOverride`.

## Rate-limit fallback and switching

Automatic provider switching is opt-in. Switching providers can change privacy boundaries, data
handling, cost, model behavior, and generated results. Review both providers' settings before
enabling it.

```yaml
autoSwitchOnRateLimit: true
maxRateLimitAgentSwitchesPerTask: 1
rateLimitFallbacks:
  claude:
    - codex
```

The default for `autoSwitchOnRateLimit` is `false`. Fallback names are validated, routing cycles are
rejected, and `dryrun` is never selected automatically unless it is explicitly listed. Every
configured fallback is executable-preflighted before the first checkpoint.

If Claude is already blocked, the example routes matching unstarted Claude prompts directly to
Codex without creating a fake Claude invocation. If Claude reports a limit during an invocation,
that output remains in its unique attempt folder, consumes no normal retry slot, and Codex starts a
fresh session against the current worktree. Verification failures after the handoff resume only the
Codex session. If no available, preflighted fallback exists, the run keeps the existing safe
`RateLimited` stop behavior.

Manual pending switches use the same run-local routing state but never alter completed tasks or the
currently running process. They are applied at the next prompt boundary. Continuing a stopped
rate-limited task appends a new attempt folder and never transfers a Claude/Codex session ID.

## Run with Claude Code

```powershell
dotnet run --project src\AgentBatchRunner -- run prompts.yaml --agent claude
```

Initial attempts use (with the configured unattended permission mode):

```powershell
claude --permission-mode acceptEdits -p "<prompt>" --output-format json
```

Retries resume the captured Claude `session_id` when available:

```powershell
claude --resume "<session_id>" --permission-mode acceptEdits -p "<retry prompt>" --output-format json
```

The permission behavior is configurable (see [Unattended execution settings](#unattended-execution-settings)). Set `claudeDangerouslySkipPermissions: true` to pass `--dangerously-skip-permissions` instead, or set `claudePermissionMode` to an empty string to omit the flag.

## Run with Codex

```powershell
dotnet run --project src\AgentBatchRunner -- run prompts.yaml --agent codex
```

Initial attempts use (with the configured sandbox so the agent can write to the workspace):

```powershell
codex exec --sandbox workspace-write "<prompt>"
```

Retries use Codex resume support (the resumed session keeps its original sandbox):

```powershell
codex exec resume --last "<retry prompt>"
```

The sandbox is configurable (see [Unattended execution settings](#unattended-execution-settings)). Set `codexFullAuto: true` to pass `--full-auto` instead, or set `codexSandbox` to an empty string to omit the flag.

If a Codex session id is detected in structured output, the adapter prefers that specific session. When no id was captured, it falls back to `resume --last` and logs a warning, because `--last` resumes the most recent Codex session globally and could attach to an unrelated session if other Codex runs happened concurrently.

An exact-session retry uses the supported positional syntax:

```powershell
codex exec resume "<session-id>" "<retry prompt>"
```

## Resume and Report

```powershell
dotnet run --project src\AgentBatchRunner -- resume
dotnet run --project src\AgentBatchRunner -- report

# Target a specific run instead of the latest:
dotnet run --project src\AgentBatchRunner -- resume --run-id 20260625-183000
dotnet run --project src\AgentBatchRunner -- report --run-id 20260625-183000
```

`resume` finds the latest run under `.agentbatchrunner/runs/`, skips tasks that already succeeded,
restores `run-routing.json`, and reruns the remaining tasks using the normalized config saved for
that run. Existing attempt folders are retained and new invocations are appended. `report`
regenerates the Markdown/JSON report for a run.

Both commands default to the most recent run and accept `--run-id <id>` to target a specific one. Run history is discovered by walking up from the current directory, so **run `resume`/`report` from the repo root** (the directory whose `.agentbatchrunner/` folder holds the runs).

## Folder Pipelines

The Windows GUI has a **Folder Pipeline** tab for discovering, validating, and running an ordered
folder of `.yaml`/`.yml` batches. The CLI exposes the same Core planner and state machine:

```powershell
dotnet run --project src\AgentBatchRunner -- folder validate C:\work\pipeline
dotnet run --project src\AgentBatchRunner -- folder plan C:\work\pipeline
dotnet run --project src\AgentBatchRunner -- folder run C:\work\pipeline --mode confirm
dotnet run --project src\AgentBatchRunner -- folder run-next --pipeline-run-id 20260718-120000
dotnet run --project src\AgentBatchRunner -- folder resume --pipeline-run-id 20260718-120000
dotnet run --project src\AgentBatchRunner -- folder status --pipeline-run-id 20260718-120000
dotnet run --project src\AgentBatchRunner -- folder report --pipeline-run-id 20260718-120000
dotnet run --project src\AgentBatchRunner -- folder skip --pipeline-run-id 20260718-120000 --file 11_phase_0_recovery.yaml --reason "Handled in another reviewed run"
dotnet run --project src\AgentBatchRunner -- folder complete-manually --pipeline-run-id 20260718-120000 --file 11_phase_0_recovery.yaml --reason "External review accepted" --evidence C:\work\review.md --satisfy-dependencies
dotnet run --project src\AgentBatchRunner -- folder start-from --pipeline-run-id 20260718-120000 --file 20_phase_1_saas_foundation.yaml
dotnet run --project src\AgentBatchRunner -- folder undo-status --pipeline-run-id 20260718-120000 --file 11_phase_0_recovery.yaml
```

`Confirm Each` is the default. `Manual` runs one selected file and stops. `Auto Advance` is opt-in
and advances only after an `Approved` JSON review with `canAutoAdvance: true`, one unambiguous
eligible next file, approved dependencies/gates, and available execution/review agents. A
successful batch exit is execution evidence, not gate approval.

Queue controls are run-local and append-only audited. `SkippedByUser` never satisfies a dependency
or gate and never executes the source YAML or its review. `ManuallyCompleted` satisfies dependencies
only with the explicit option and approves a gate only through a separate explicit override. CLI
gate approval additionally requires `--approve-gate`, `--confirm-gate-override`,
`--evidence <path>`, and `--override-source <source>`. GUI checkboxes default to false, and the gate checkbox is shown only
for files declaring a gate.

`Start From Selected` first previews all earlier pending files. Unmet prerequisites block the action
and can be selected in the GUI. Only independent, currently eligible earlier files are marked
`SkippedByUser` after confirmation; ineligible earlier files remain pending. Undo restores the
persisted prior status only when no dependent file has progressed. Every action records actor,
timestamp, reason, evidence, dependency/gate flags, source, and reversal linkage in state, summary,
queue data, and the pipeline report.

Dependency satisfaction is intentionally narrow: `Approved` satisfies dependencies; successful
execution without review does so only when neither review nor a gate is required; manual completion
does so only when explicitly accepted. A manual gate override is independent from dependency
satisfaction and never follows from skipping or starting later in the queue.

Optional top-level metadata remains backward compatible with existing prompt files:

```yaml
pipeline:
  id: ACE-PHASE-0-RECOVERY
  title: ACE AdPilot Phase 0 recovery
  phase: PHASE-0
  order: 10
  enabled: true
  dependsOn: []
  gate:
    id: G0
    requiredForNextPhase: true
    criteria:
      - Required architecture decisions are recorded.
  review:
    required: true
    agent: claude
    independentAgentPreferred: true
    autoGenerate: true
  next:
    onApproved: 20_phase_1_saas_foundation.yaml
    onApprovedWithWarnings: manual
    onBlocked: stop
    onNeedsHumanDecision: stop
```

Discovery excludes `_`-prefixed files, disabled files, `.agentbatchrunner`, and generated
`.review.yaml`/`.review-Rn.yaml` files. Validation blocks duplicate IDs/orders, missing or circular
dependencies, invalid next references, and self-references. Files without `pipeline` metadata stay
valid, sort by filename, use optional review unless `--require-review` is supplied, and require
manual next-file selection.

After an execution requiring review, AgentBatchRunner generates a review YAML under the pipeline
run, invokes the configured reviewer once in Claude `plan` mode or Codex `read-only` mode, parses a
JSON verdict, writes a separate Markdown review, and verifies both artifacts. Supported verdicts
are `Approved`, `ApprovedWithWarnings`, `Blocked`, `NeedsHumanDecision`,
`PrerequisiteMissing`, `ReviewFailed`, `Canceled`, and `RateLimited`. Only `Approved` can advance by
default. Reviews compare product Git state before/after; a mutation is preserved for inspection and
stops as `ReviewFailed` without reset.

Provider selection can affect privacy, cost, and output behavior. Execution and review routing are
separate GUI/CLI overrides; use an independent provider only after reviewing those implications.
Review recommendations, metadata `next` rules, and dependency order are evaluated in that order.
Ambiguous candidates always require a person. A repair review may recommend re-reviewing an
already executed blocked gate; the source batch is not executed again and prior review evidence is
never overwritten.

Pipeline state is persisted after each boundary under:

```text
.agentbatchrunner/pipelines/<pipelineRunId>/
  pipeline-state.json
  pipeline-summary.json
  pipeline-report.md
  discovered-files.json
  queue.json
  execution-diffs/
  generated-reviews/
  review-runs/
  reviews/
```

The runner stops on gate blockers, human decisions, missing prerequisites, rate limits, review
failures, cancellation, ambiguous selection, or the automatic-transition cap (20 by default). It
never commits, resets, checks out, deletes, or force-pushes product work.

Agents may also report a workflow outcome in JSON or a structured footer. `Blocked`,
`NeedsHumanDecision`, `PrerequisiteMissing`, and `RateLimited` do not consume the normal retry
budget or run verification as an ordinary failure:

```json
{
  "agentOutcome": "Blocked",
  "blockerCode": "PHASE_PREREQUISITE_MISSING",
  "blocker": "Human approval has not been recorded.",
  "recommendedNext": "12_phase_0_gate_repair.yaml"
}
```

## Unattended execution settings

These optional top-level keys control how agents are launched for unattended batches. The defaults
are shown:

```yaml
defaultAgentTimeoutSeconds: 1800       # agent CLI timeout, in seconds
defaultVerifyTimeoutSeconds: 900       # per verification command timeout, in seconds
autoSwitchOnRateLimit: false           # opt in only after reviewing provider privacy/cost
maxRateLimitAgentSwitchesPerTask: 1
# rateLimitFallbacks:
#   claude:
#     - codex
claudePermissionMode: acceptEdits      # Claude --permission-mode value ("" omits the flag)
claudeDangerouslySkipPermissions: false # true => --dangerously-skip-permissions (overrides the mode)
codexSandbox: workspace-write          # Codex --sandbox value for the initial run ("" omits the flag)
codexFullAuto: false                   # true => --full-auto (overrides the sandbox flag)
minimumCodexVersion: 0.144.5           # minimum accepted codex-cli version
# codexExecutablePath: C:\Tools\OpenAI Codex\codex.exe
# claudeExecutablePath: C:\Tools\Claude\claude.exe
```

Agent and verification timeouts are required to be positive. A timed-out process is terminated,
recorded with exit code `124`, and treated as a failed attempt so the normal retry loop applies.
Verification timeouts apply to each verification command independently and stop the remaining
verification commands for that attempt.

Prompts can override either timeout:

```yaml
prompts:
  - id: P001
    title: Longer integration test
    prompt: >
      Make the integration suite pass.
    verify:
      - dotnet test tests\Integration.Tests\Integration.Tests.csproj
    agentTimeoutSeconds: 2400
    verifyTimeoutSeconds: 1800
```

## Agent executable resolution and preflight

Executable paths are optional and must be absolute when supplied. Resolution happens once before
the first checkpoint or prompt, in this order:

1. `codexExecutablePath` or `claudeExecutablePath` in YAML.
2. `AGENTBATCHRUNNER_CODEX_PATH` or `AGENTBATCHRUNNER_CLAUDE_PATH`.
3. On Windows, native Codex at `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` when present.
4. `PATH`/`PATHEXT` lookup.

AgentBatchRunner retains and uses the resulting absolute path for the run and normalized resume
configuration. It runs `<agent> --version` locally and does not make a provider request during
preflight. Codex output must be parseable as `codex-cli <version>` and meet
`minimumCodexVersion`; Claude must return a parseable version. Missing executables, nonzero or
unparseable version output, stale WSL-guidance launchers, and unsupported Codex versions stop the
entire run before task checkpoints are created.

If a running agent later reports `requires a newer version of Codex` or cannot launch, that is a
single run-level `ToolchainFailure`: it is not retried, untouched prompts are marked `Skipped`, and
the report records one actionable blocker. Genuine prompt/verification failures, timeouts, and rate
limits keep their existing retry or stop behavior.

On Windows, inspect all launchers and the active version with:

```powershell
where.exe codex
codex --version
& "$env:LOCALAPPDATA\Programs\OpenAI\Codex\bin\codex.exe" --version
```

Restart AgentBatchRunner after changing `PATH`; an already-running GUI keeps the environment it
inherited at startup. AgentBatchRunner never updates or installs an agent CLI automatically.

## Example prompts.yaml

```yaml
project: ATACS.Security
repoPath: C:\REPO\ATACS
defaultAgent: claude
defaultMaxRetries: 3
defaultAgentTimeoutSeconds: 1800
defaultVerifyTimeoutSeconds: 900
minimumCodexVersion: 0.144.5
autoSwitchOnRateLimit: true
maxRateLimitAgentSwitchesPerTask: 1
rateLimitFallbacks:
  claude:
    - codex
# Optional absolute paths override environment/native/PATH discovery:
# codexExecutablePath: C:\Tools\OpenAI Codex\codex.exe
# claudeExecutablePath: C:\Tools\Claude\claude.exe

prompts:
  - id: P001
    title: Fix nullable YearId issue
    agent: claude
    prompt: >
      Fix nullable YearId dereference in ReportsController.
      Do not change business logic.
    verify:
      - dotnet build ATACS.Security.sln
      - dotnet test ATACS.Security.Tests\ATACS.Security.Tests.csproj
    maxRetries: 3
    agentTimeoutSeconds: 1800
    verifyTimeoutSeconds: 900

  - id: P002
    title: Add regression test
    agent: codex
    prompt: >
      Add a characterization test for dashboard default year behavior.
    verify:
      - dotnet build ATACS.Security.Tests\ATACS.Security.Tests.csproj
```

## Output Layout

```text
.agentbatchrunner/
  runs/
    20260625-183000/
      run-config.normalized.json
      run-routing.json
      run-summary.json
      final-report.md
      tasks/
        P001/
          prompt.md
          status.json
          git-status-before.txt
          git-diff-before.patch
          git-diff-after.patch
          checkpoint.txt
          attempts/
            attempt-1/
              agent-output.txt
              verification.log
              status.json
```

## Known Limitations

- Verification commands run through the local shell and are trusted developer input.
- The MVP creates checkpoint branches but does not auto-commit, reset, delete files, or force-push.
- Retry feedback includes verification output, capped to the last 24,000 characters to avoid oversized CLI invocations.
- Claude session resume depends on `session_id` appearing in JSON output.
- Codex exact-session resume depends on session identifiers appearing in structured output; otherwise it uses `codex exec resume --last` and logs a warning.
- Agent and verification timeouts terminate the process tree on timeout where supported. Commands that ignore termination or spawn detached children may still require manual cleanup.
- A prompt with no `verify` commands is recorded as `UnverifiedSuccess` (the agent succeeded but the result was not automatically checked) rather than `Succeeded`.
- Unattended permission/sandbox flags target current `claude` and `codex` CLI syntax; if your installed CLI differs, override the relevant settings (or set them to empty) so the correct flags are sent.
- CLI `validate` checks YAML/configuration only. CLI `run` performs executable preflight; GUI `Validate` performs both configuration validation and preflight.
- Prompts, captured stdout/stderr, JSON state, logs, and Markdown reports are written and read as UTF-8 without a BOM.
- Automatic and manual provider switches preserve the current worktree; partial changes from a rate-limited provider are not reset. Review the final routing report and Git diff carefully.
- Folder review write protection relies on the current Claude `plan` and Codex `read-only` CLI modes plus a before/after Git-state guard. A misbehaving external process could still touch files; AgentBatchRunner reports and preserves such changes rather than resetting them.
- Generated review artifact verification commands currently target Windows PowerShell, matching the WPF folder-pipeline milestone.
- Recovery never silently reruns an execution that stopped before producing `run-summary.json`; inspect that run's artifacts and choose the next action manually.
