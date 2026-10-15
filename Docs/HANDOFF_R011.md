# R-011 — WPF supplementary account details

Real dashboard now contains a separate SupplementaryDetailsView immediately after GaugeRow.
GaugeControl, gauge XAML, quota calculations and widget remain unchanged by this task. Plan,
credits, limit reasons, reset count/known expiry and activity use existing snapshots/responses.
Missing values remain unknown; no sample data or redemption action in the real app.

## Files

- Presentation: SupplementaryDetailsView.xaml/.cs, ViewModels/SupplementaryDetailsViewModel.cs,
  ProviderTabViewModel.cs and MainWindow.xaml. New section, metadata partitioning, read-only
  expandable details, refresh-state cleanup and expansion persistence.
- Infrastructure: CodexQuotaMapper.cs maps root reset count and optional activity summary fields.
- Infrastructure/Presentation synthetic tests; tools/AIMonitor.ScreenEvidence/Program.cs.
- Requirements.md, Docs/ARCHITECTURE.md and independent review reports.

## Review

Agy Gemini 3.8 Flash High wrote source/tests. Initial CLI command wait stalled without a running
dotnet process and was interrupted. Replacement writer used file tools only, then exited. Claude
Fable 5.1 high and Codex independently reviewed. Confirmed fixes integrated: fabricated Claude plan,
setup visibility, API-mode wording, expansion persistence, missing activity note, narrow layout,
unrelated-provider metadata filtering, Int64 overflow and strict Unix-seconds expiry. Capped-list
minimum is labelled known expiry; UTC date retains explicit Z.

## Verification

- `dotnet build AIMonitor.sln -c Release --no-restore`: PASS, 0 warnings/errors.
- `dotnet test AIMonitor.sln -c Release --no-restore --verbosity minimal`: PASS **1,457** tests
  (Domain 218, Application 106, Infrastructure 768, Presentation 365), no failures/skips.
- `dotnet format AIMonitor.sln --verify-no-changes --no-restore`: PASS.
- `dotnet run --project tools/AIMonitor.ScreenEvidence -c Release -- tests/Fixtures/Parity/screens.json artifacts/verification/r011-wpf`:
  PASS, 40 synthetic WPF renders. OpenAI dashboard and expanded narrow dark view visually inspected.
- `git diff --check`: PASS, existing line-ending notices only.

Initial verification found xUnit2031 in a new assertion and a test expecting old unknown-data wording.
Both fixed, then full suite rerun successfully. Automated tests/render use synthetic data only.

## Launch and limits

Old workspace publish process PID 27476 was path-checked and restarted after approval. Windows UI
helper failed twice, so a scoped process restart was used. New Release process PID 19736 was verified
at src/AIMonitor.Presentation.Wpf/bin/Release/net10.0-windows/AIUsageMonitor.exe with a main window;
left open for owner inspection. Writing/review CLI sessions closed. Installer/publish not rebuilt.

Contract verified against [official App Server docs](https://learn.chatgpt.com/docs/app-server)
sections 6/7 (2026-10-15). Reset count is authoritative; optional activity follows existing IncludeHistory.
Real account values and owner acceptance remain pending. Full accessibility audit not performed;
Expander retains OS template. AGY-Usage.md unchanged, separate future AGY/Gemini work. No credentials,
login changes, reset consumption, deployment, commit or push.
