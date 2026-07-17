# Codex repair prompt — AgentBatchRunner routing, executable preflight, and UTF-8

Run this prompt directly with the verified native Codex CLI from the AgentBatchRunner repository. Do **not** submit it through AgentBatchRunner while AgentBatchRunner is the component being repaired.

Suggested PowerShell invocation:

```powershell
Set-Location 'C:\TEMP\AgentBatchRunner'

$prompt = Get-Content 'C:\path\to\AgentBatchRunner_Fix_Routing_Preflight_UTF8_Codex_Prompt.md' -Raw -Encoding UTF8
codex exec --sandbox workspace-write $prompt
```

---

You are repairing AgentBatchRunner, a .NET 8 sequential YAML prompt runner with a WPF GUI and CLI. Work only in the AgentBatchRunner repository. Inspect the solution, README, tests, applicable `AGENTS.md` files, existing process-runner abstractions, configuration models, YAML normalization, CLI option handling, WPF view models, and report generation before editing.

Preserve all unrelated and pre-existing user changes. Do not commit, push, publish, deploy, change credentials, call real Claude/Codex services from tests, or weaken sandbox/approval defaults. Use the smallest coherent implementation that fixes the behavior below while retaining backward compatibility with existing YAML files and CLI usage.

## Confirmed failures

There are four related defects.

### 1. The WPF Agent selector silently overrides per-prompt YAML routing

The phase YAML contains mixed routing:

- P0-001 through P0-012 specify `agent: claude`.
- P0-013 through P0-018 specify `agent: codex`.
- `defaultAgent: codex` is only a fallback when a prompt has no `agent`.

Selecting `codex` in the GUI caused every task to execute with Codex. Selecting `claude` caused every task to execute with Claude. The task table also displayed the globally selected agent for every prompt. This makes a mixed-agent phase unsafe and contradicts the YAML.

Required effective-agent precedence:

1. An explicit run-level override chosen deliberately by the user or supplied by CLI `--agent`.
2. The prompt's own `agent` value.
3. The YAML `defaultAgent`.
4. Otherwise validation must fail with a clear error.

Absence of a run-level override must remain `null`; it must not be replaced with the currently selected GUI agent or the YAML default during validation, normalization, display, or execution.

### 2. The WPF process can launch a stale Codex executable

The machine contains both:

- Native current CLI: `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`, version `codex-cli 0.144.5`.
- Stale/compatibility launcher: `%USERPROFILE%\.local\bin\codex.exe`, which previously reported `0.57.0` or printed WSL installation guidance.

A fresh PowerShell resolved and successfully ran native Codex 0.144.5, but an already-running AgentBatchRunner process inherited an older PATH and executed 0.57.0. That failed with:

```text
The 'gpt-5.6-sol' model requires a newer version of Codex.
```

AgentBatchRunner currently records only `codex` as the executable instead of resolving, validating, retaining, and reporting the absolute executable used.

### 3. Toolchain failures are retried for every prompt

Missing executables, an unparseable version response, an unsupported/outdated CLI, or the provider response `requires a newer version of Codex` are environment/toolchain failures. They are not task failures and should not consume every prompt retry or turn every prompt into `NeedsHumanReview`.

### 4. Process output is not consistently UTF-8

Unicode input is correct in normalized JSON, but captured Claude output contains mojibake such as:

```text
â€”
â€“
```

instead of em and en dashes. Prompts, stdout, stderr, JSON artifacts, logs, and reports must preserve UTF-8 end to end.

## Implement the repair

### A. Centralize effective-agent selection

Create or reuse one policy/service for resolving the effective agent. Validation preview, task-table display, normalized run configuration, execution, resume, and report generation must use the same policy.

Add a WPF Agent option named `From YAML (recommended)` (or an equivalent unambiguous label). It must:

- map to a `null` run-level override;
- be the default selection when the GUI starts and when a YAML file is loaded;
- preserve each prompt's `agent`, falling back to `defaultAgent` only when absent;
- show the effective agent for every row before Run is enabled.

Keep explicit `claude`, `codex`, and `dryrun` overrides if they are existing supported behavior. When one is selected, display a conspicuous non-blocking warning such as `Overrides the agent for every prompt in this run`. Do not silently persist a previous explicit override when a different YAML file is loaded.

CLI behavior must remain:

- no `--agent` means no override and therefore From YAML;
- `--agent <name>` is an intentional global override.

Do not change existing YAML semantics. Add validation for unknown agent names and for prompts that have neither a prompt agent nor a usable default.

### B. Add deterministic executable configuration and resolution

Introduce a testable executable resolver instead of passing the bare strings `codex` and `claude` directly to `ProcessStartInfo`.

Support optional configuration for absolute executable paths. Choose names that fit the existing configuration conventions, for example:

```yaml
codexExecutablePath: C:\Users\name\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe
claudeExecutablePath: C:\Users\name\.local\bin\claude.exe
minimumCodexVersion: 0.144.5
```

The fields must be optional and backward compatible. Do not hardcode the example username or ACE AdPilot paths.

Also support environment overrides if consistent with the codebase, for example:

```text
AGENTBATCHRUNNER_CODEX_PATH
AGENTBATCHRUNNER_CLAUDE_PATH
```

Use a documented precedence such as:

1. Explicit configuration/CLI executable path.
2. Environment override.
3. On Windows for Codex, the native OpenAI location under `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` when it exists.
4. PATH/PATHEXT resolution.

Resolve to an absolute path once during preflight and use that exact path throughout the run and resume. Correctly handle spaces in paths. Never choose a stale executable merely because it occurs earlier in PATH when the current native OpenAI Windows executable is present.

Do not shell out through an unsafe concatenated command string. Use `ProcessStartInfo.ArgumentList` or the repository's equivalent safe structured-argument mechanism.

### C. Add run-level agent preflight

Before creating task checkpoints or running any prompt:

1. Resolve all effective agents used by the run.
2. Resolve each required executable to an absolute path.
3. Execute a lightweight local version command such as `codex --version` or `claude --version` through the same process abstraction used by production.
4. Parse and validate the result.
5. Record the resolved executable path and version in the GUI, normalized run configuration, run summary, and final report without exposing credentials.

For Codex:

- accept version output such as `codex-cli 0.144.5`;
- reject missing, nonzero, unparseable, WSL-guidance-only, or below-minimum results;
- make the minimum version configurable rather than assuming `latest` forever;
- provide an actionable error containing the resolved path, observed version/output summary, required minimum, and remediation;
- never invoke `codex update` or install software automatically.

For Claude, verify that the configured executable launches and produces a parseable version. Do not introduce an arbitrary minimum unless the repository already has a supported-version policy.

Preflight must use fakes in automated tests and must not spend tokens or call a provider API.

### D. Fail fast on non-retryable toolchain failures

Introduce or reuse an explicit failure classification such as `PreflightFailed`, `ConfigurationError`, or `ToolchainFailure`.

The run must stop before the first task if preflight fails. During an active run, stop the batch immediately if execution proves that the resolved toolchain is unusable, including:

- executable not found or cannot be launched;
- executable/version mismatch;
- response containing `requires a newer version of Codex`;
- invalid process startup configuration.

Do not retry these as prompt-verification failures. Do not mark every remaining prompt `NeedsHumanReview`. Mark remaining tasks as not started/skipped due to the single run-level blocker, preserve diagnostic artifacts, and produce one actionable report. Rate limits and genuine prompt/test failures must retain their existing retry/resume behavior.

### E. Preserve UTF-8 end to end

Audit process input/output and artifact writes. On .NET process launches, configure UTF-8 explicitly where applicable, including `StandardOutputEncoding` and `StandardErrorEncoding`, and use structured arguments rather than manually quoted prompt strings.

Ensure the following survive round trips unchanged:

```text
P0-001 — Product charter
10–20 teams
“quoted text”
```

Write logs, JSON, Markdown reports, and agent output as UTF-8 consistently. Do not corrupt already-valid source files while repairing display or capture.

## Required tests

Use the repository's existing test architecture and fakes. Add focused regression coverage for at least:

1. From YAML with prompt agents `claude`, `codex` produces the same mixed sequence.
2. From YAML falls back to `defaultAgent` only when prompt `agent` is absent.
3. Explicit GUI/CLI override intentionally replaces all prompt agents and exposes a warning/override indicator.
4. Loading another YAML resets the GUI to From YAML instead of retaining a silent override.
5. Validation preview, execution, resume, and report use the same effective-agent result.
6. Windows resolver prefers an explicitly configured path.
7. Windows resolver prefers the existing native OpenAI Codex path over a stale PATH candidate.
8. Paths containing spaces launch correctly.
9. Codex `0.57.0` is rejected when the configured minimum is `0.144.5`; `0.144.5` and newer pass.
10. WSL-guidance-only or unparseable `--version` output fails preflight.
11. Preflight failure starts zero prompts, creates zero task checkpoint branches, performs zero retries, and produces one clear run-level diagnostic.
12. `requires a newer version of Codex` is non-retryable and stops the batch.
13. Genuine verification failures and rate limits preserve existing retry/resume semantics.
14. Em dash, en dash, smart quotes, multiline prompts, and paths with spaces remain valid UTF-8 in captured output and artifacts.
15. Existing YAML without the new executable/version fields remains valid.

No automated test may invoke a real Claude/Codex process, network request, provider account, or paid model. Inject/fake the resolver and process runner.

## UI and reporting acceptance criteria

Before Run is enabled, the GUI must make all of these visible:

- routing mode: `From YAML` or `Override: <agent>`;
- each task's effective agent;
- resolved executable path and detected version for every required real agent, after preflight;
- preflight state and actionable failure message.

The final report and normalized run artifacts must distinguish:

- configured prompt agent;
- default agent;
- optional run override;
- effective agent;
- resolved executable and version;
- prompt/test failure versus run-level toolchain failure.

Do not log access tokens, authentication files, full environment variables, or secrets.

## Documentation and verification

Update the README/configuration documentation with:

- From YAML versus explicit override semantics;
- precedence rules;
- optional executable-path and minimum-version configuration;
- native Windows Codex troubleshooting using `where.exe codex` and `codex --version`;
- why AgentBatchRunner must be restarted after PATH changes;
- fail-fast behavior.

Run the complete relevant build and test suite. If WPF tests require Windows and the current environment cannot execute them, still compile all portable projects, add the tests, and report the exact unexecuted Windows-only verification rather than claiming success.

Do not commit or push.

Finish with:

- root causes found;
- implementation summary;
- files changed;
- tests/build commands and exact results;
- any migrations or backward-compatibility notes;
- remaining risks;
- a short manual Windows acceptance checklist for the user.
