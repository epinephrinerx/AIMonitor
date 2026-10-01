# Phase 2 handoff — Architecture and verification design

## Objective

เขียนเอกสาร architecture และ testing ที่พร้อมใช้สำหรับ AIMonitor 2.0 ซึ่งย้ายแอป Windows จาก Python/PySide6 เป็น C# บน .NET 10 โดยยังไม่สร้าง application code

## Acceptance criteria

- `Docs/ARCHITECTURE.md` ไม่มี template placeholders และอธิบาย system boundaries, dependency direction, runtime flows, storage, interfaces และ Windows integrations ที่ตกลงแล้ว
- `Docs/TESTING.md` ไม่มี template placeholders และระบุคำสั่ง/ระดับการทดสอบ/fixture isolation/CI gates ที่เป็นจริงสำหรับ solution ที่วางแผนไว้
- สร้าง ADR ที่จำเป็นใน `Docs/adr/` อย่างน้อยสำหรับ UI/architecture, settings migration และ packaging/upgrade
- ระบุ proposed solution/project topology โดยไม่สร้าง `.sln`, `.csproj` หรือ source code
- ไม่เปลี่ยน Requirements, workflow, repository instructions หรือ feature-parity baseline

## Approved decisions

- Runtime: .NET 10 LTS for Windows Desktop
- UI: WPF
- Architecture: layered architecture + MVVM
- Dependency direction: WPF Presentation → Application → Domain; Infrastructure implements inward-facing ports
- Settings: JSON under LocalAppData; Windows user-bound DPAPI for secrets
- Migration: one-time, read-only import from legacy Registry; never delete legacy values during migration
- Packaging: Inno Setup, self-contained win-x64
- Upgrade: in-place from Python installer; preserve legacy AppId and prevent duplicate startup/tray/poller state
- Tests: xUnit; hand-written fakes by default; synthetic/disposable fixtures; no production access or real credentials
- Visuals: custom WPF drawing for charts and gauges
- Tray: .NET Windows Forms NotifyIcon hosted by WPF app
- Single instance: per-user named mutex + named pipe activation
- Startup: HKCU Run; after initialization Windows Startup Apps state is authoritative
- Diagnostics: size/count-bounded rolling logs under LocalAppData with mandatory secret/payload redaction

## Scope

Writing Owner may edit/create only:

- `Docs/ARCHITECTURE.md`
- `Docs/TESTING.md`
- `Docs/adr/*.md`

Read-only context:

- `AGENTS.md`, `CLAUDE.md`, `Requirements.md`
- `Docs/AI_WORKFLOW.md`, `Docs/FEATURE_PARITY.md`

## Constraints and non-goals

- Do not create application/test/build code, solution files or package manifests.
- Do not modify `D:\Dev\Apps\AIMonitor`.
- Do not commit, push, publish, install dependencies or change machine configuration.
- Do not invent public API capabilities or provider data. Preserve the no-invented-data and partial-success rules in `FEATURE_PARITY.md`.
- For OpenAI/Codex integration claims, use current official OpenAI documentation only. Mark experimental/unstable contracts explicitly.
- Keep the diff limited to Phase 2 documentation.
- Replace irrelevant template prose instead of appending a second architecture beside it.

## Verification requested from writer

- Search edited files for remaining bracket placeholders.
- Check all commands and planned paths are internally consistent.
- Report files changed, checks run, unresolved decisions and known risks.

## Review contract

Coding Owner will inspect the resulting diff independently. Reviewer findings must cite file/line, impact and evidence; the reviewer must not edit files.
