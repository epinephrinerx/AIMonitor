# Phase 3 handoff — .NET solution and test foundation

## Objective

สร้างโครงสร้าง solution ของ AIMonitor 2.0 ตามสถาปัตยกรรม Phase 2 ให้ restore/build/test/format ผ่านบน .NET 10 โดยยังไม่ implement provider, storage, Windows integration หรือ UI behavior จริง

## Acceptance criteria

- มี `AIMonitor.sln` แบบ classic solution และ `global.json` pin SDK 10.0.401 ด้วย patch-compatible roll-forward
- มี projects ตาม topology ที่อนุมัติ: Domain, Application, Infrastructure, Presentation.Wpf และ test projects 4 ชั้น + TestSupport
- Project references บังคับ dependency direction ตาม `Docs/ARCHITECTURE.md`
- Domain/Application ไม่ target Windows-specific TFM และไม่ reference WPF/Infrastructure
- WPF project target `net10.0-windows`, เปิด WPF และ Windows Forms สำหรับ `NotifyIcon` ในอนาคต
- Nullable, implicit usings, deterministic build และ warnings-as-errors ถูกกำหนดจากส่วนกลาง
- xUnit test skeleton อย่างน้อยหนึ่ง test ต่อ test projectพิสูจน์ว่า runner/discovery ทำงานจริง โดยไม่แตะ network, Registry จริง, LocalAppData จริง หรือ credentials
- `dotnet restore`, `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` ผ่านจริง
- ปรับ `Docs/ARCHITECTURE.md`/`Docs/TESTING.md` เฉพาะถ้อยคำจาก proposed/planned ให้ตรงกับไฟล์ที่สร้างจริงและคำสั่งจริง

## Scope

Writing Owner may create/edit:

- `AIMonitor.sln`, `global.json`, `Directory.Build.props`, `.gitignore`
- `src/AIMonitor.Domain/**`
- `src/AIMonitor.Application/**`
- `src/AIMonitor.Infrastructure/**`
- `src/AIMonitor.Presentation.Wpf/**`
- `tests/AIMonitor.Domain.Tests/**`
- `tests/AIMonitor.Application.Tests/**`
- `tests/AIMonitor.Infrastructure.Tests/**`
- `tests/AIMonitor.Presentation.Wpf.Tests/**`
- `tests/AIMonitor.TestSupport/**`
- `Docs/ARCHITECTURE.md`, `Docs/TESTING.md`

Do not edit Requirements, workflow, parity matrix, ADRs, handoff/review artifacts or repository instructions.

## Required topology

- `AIMonitor.Domain`: `net10.0`, no project references
- `AIMonitor.Application`: `net10.0`, references Domain
- `AIMonitor.Infrastructure`: `net10.0-windows`, references Application and Domain
- `AIMonitor.Presentation.Wpf`: `net10.0-windows`, WPF executable, references Application and Infrastructure; Windows Forms enabled only for future `NotifyIcon`
- `AIMonitor.TestSupport`: library for reusable synthetic fixtures/fakes; references Domain and Application only
- Domain tests reference Domain + TestSupport
- Application tests reference Application + Domain + TestSupport
- Infrastructure tests reference Infrastructure + Application + Domain + TestSupport
- Presentation tests reference Presentation + Application + Domain + TestSupport

Keep generated code minimal. Delete meaningless template classes/tests and replace them with small architecture-smoke tests that assert observable assembly/load/reference basics without testing implementation details.

## Constraints

- Use xUnit and Microsoft.NET.Test.Sdk from standard `dotnet new xunit` templates; do not add mocking, DI, MVVM, logging, chart or installer packages in this phase.
- Do not implement business behavior or mark any PAR item Verified.
- Do not read or copy real provider credentials/settings.
- Do not run the WPF application; startup behavior is not implemented yet.
- Do not commit, push, publish, install additional tools or change machine configuration.
- Preserve all existing files and user work.

## Verification and handoff

Run commands from repository root and report exact results:

1. `dotnet restore AIMonitor.sln`
2. `dotnet build AIMonitor.sln --no-restore`
3. `dotnet test AIMonitor.sln --no-build`
4. `dotnet format AIMonitor.sln --verify-no-changes --no-restore`

Also report files created/changed, package versions selected by templates, warnings, open questions and remaining risks. Do not claim success for commands not run.
