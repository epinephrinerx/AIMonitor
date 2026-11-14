# R-006 Claude independent review — 2026-10-15

Reviewer: Claude Fable 5.1, High, read-only CLI review. Completed without reading Codex findings. No tests or live provider calls run by reviewer.

## Findings returned

- Medium: Gemini 401 guidance tells service-account users to sign in to Gemini CLI, which cannot repair the selected service-account source.
- Medium: Gemini 403 feedback assumes a service account and Monitoring Viewer permission regardless of selected source or failing host. Resource Manager discovery failures need explicit-project guidance.
- Low: Limited CLI credential retains a token despite the documented empty-value invariant for unusable credentials.
- Low: Missing-scope diagnostics explain the missing permission but not recovery; SetupHint omits project environment options.
- Low: OpenAI 401 advice assumes a Settings key even for environment or CLI key sources.
- Low: Invalid project environment values produce generic HTTP failures; values are URL-escaped, so no injection was found.

## Meaningful coverage gaps

Missing-scope live client must avoid HTTP; Limited CLI must not hide a usable later gcloud service account; explicit/credential project priority must beat environment; auth failure must preserve source/account/candidates with source-appropriate hints; KeyGuidance property notification needs coverage.

## Triage

Accept both Medium findings, source-aware OpenAI guidance, actionable missing-scope/setup advice, empty unusable token invariant, and the listed regression coverage. Project syntax validation is deferred: accepted project identifiers require a separate contract and the existing override/credential paths have the same behavior; preserve safe URL escaping. Combine with Codex history-auth detection finding for the writer fix pass. No high/critical issue reported.
