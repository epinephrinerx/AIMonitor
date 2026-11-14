# R-008 login guidance review — 2026-10-15

Scope: OpenAI SetupHint strings in ProviderMeta and OpenAiLiveQuotaClient; existing verbatim-copy assertion updated accordingly. No authentication behavior or new metrics implemented.

Codex reviewed copy against fetched official https://learn.chatgpt.com/docs/auth and https://learn.chatgpt.com/docs/cli/reference . Browser/device-code commands, optional device-code enablement, login-status mode check, Refresh, file-backed read-only auth limitation, Admin-key spend and OAuth precedence agree with current implementation. Extra-data assessment narrowed to documented fields that current mapper does not already display; speculative cross-provider and actual per-model cost claims removed.

Claude Fable 5.1 High independently reviewed the two strings without reading Codex findings. No correctness blocker; noted optional CODEX_HOME path override is not described and the expired-login priority phrasing could be clearer. Reviewer did not run tests or fetch documentation and did not verify runtime login. CLI exited 0. These are copy observations, not behavior defects; no auth changes were made.

Synthetic visual QA: rendered 36 WPF screens to artifacts/verification/r008/wpf; inspected connect-openai-light.png. All login steps and Save/Cancel are visible, with wrapped text. Writer build/format and existing 1,403-test suite passed. Local packaging/preview results are recorded in HANDOFF_R008.md. Owner real-account/UI acceptance remains pending.
