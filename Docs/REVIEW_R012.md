# R-012 review and integration

Date: 2026-10-16. Scope: replace active Gemini adapter with AGY CLI usage pools.

Independent Claude Fable 5.1 high review completed through CLI; Codex performed a separate source review and integrated fixes. Reviewers did not run account-backed automated tests.

| Finding | Resolution |
|---|---|
| High: incompatible tests and overlapping implementations from the first writer | Retained superseded new source/tests under ignored artifacts, leaving one active runner/mapper/client contract. Full solution compiles and tests pass. |
| Medium: AGY executable lookup might miss installed CLI | Read-only `where.exe agy` confirmed a native agy.exe on this machine. No shell or cmd fallback introduced. |
| Medium: Gemini UI assertions remained | Updated active AGY tab/card, keyless dialog, badge and settings expectations. |
| Medium: timeout should not imply an unconfigured login | Timeout/invalid response are Limited with actionable sanitized errors. |
| Low: process pipe disposal and relative PATH directories | Explicitly dispose both readers; ignore relative PATH entries. |
| Codex: late cancellation and unsafe exception messages | Check cancellation after runner completion; map known failure kinds to fixed messages. |
| Codex: potentially excessive regex work | Use nonbacktracking ISO timestamp validation with bounded JSON input. |
| Visual inspection: long shared-pool titles touched adjacent cards | Shortened known display names to Gemini and Claude / GPT; gauge control unchanged. |

The first writer's CLI was interrupted, but its backend continued writing briefly. No matching task process remained on subsequent inspection. Superseded files were preserved rather than merged, and the final active file set was checked after passing verification. No claim is made that a remote cancellation API confirmed task termination.

Remaining limits: legacy Gemini infrastructure/tests remain unregistered to preserve prior dirty work; unknown future AGY groups retain upstream names and may require display tuning. Account-backed visual acceptance remains with the user. No release/installer/deployment work was performed.
