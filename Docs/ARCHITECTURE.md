# Architecture

เอกสารนี้อธิบายโครงสร้างและขอบเขตของ **AIMonitor 2.0** — การย้าย AIMonitor จาก Python/PySide6 เป็น C#
บน .NET 10 สำหรับ Windows Desktop (`Requirements.md` R-001) อัปเดตเอกสารนี้เมื่อมีการเปลี่ยน
service boundary, data flow, storage, interface หรือ dependency direction

สถานะ ณ ตอนเขียน: **Phase 5C Windows integrations เสร็จสิ้น** — มี `AIMonitor.sln`/`.csproj` และ solution skeleton ตาม
โครงสร้างด้านล่างจริงแล้ว Phase 4A เพิ่ม provider-neutral domain contract (`Meter`, `Severity`,
`MeterReadingOrder` ฯลฯ), Phase 4B เพิ่ม `AIMonitor.Infrastructure.Providers.Claude.ClaudeQuotaParser` —
pure JSON parsing boundary (ใช้ `System.Text.Json` เท่านั้น ไม่มี dependency ใหม่) ที่แปลง Claude OAuth
usage payload (ทั้ง current `limits` shape และ legacy top-level block shape) เป็น `Meter`, Phase 4C เพิ่ม
`IProviderQuotaClient` port ใน `AIMonitor.Application`, read-only Claude Code credential discovery
(`IClaudeCredentialReader`/`ClaudeCredentialFileReader`, size-capped async strict-UTF-8 file read) และ
`ClaudeLiveQuotaClient` — Infrastructure adapter ที่เรียก Claude OAuth usage endpoint ผ่าน injected
`HttpClient` แล้วคืน `ProviderSnapshot` แบบ cancellable และ Phase 4D เพิ่ม pricing business rule
(`AIMonitor.Domain.ClaudeModelPricing`/`UsageCounters`/`UsageMetric`/`UsageValueCaveatFormatter` —
EXACT/ESTIMATED/UNKNOWN ตาม PAR-007), `AIMonitor.Infrastructure.Providers.Claude.ClaudeTranscriptStore`
— incremental read-only JSONL ingestion ของ Claude CLI transcripts พร้อม explicit byte-offset checkpoint
ต่อไฟล์, de-duplication ตาม message id, aggregation ตาม local day/model/project และ retention/eviction
ที่มีขอบเขต (PAR-006) — และผูกเข้ากับ `ClaudeLiveQuotaClient.GetSnapshotAsync` เมื่อ
`ProviderSnapshotRequest.IncludeHistory` เป็นจริง: history fetch เป็นคนละ try/catch จาก quota fetch
เสมอ และรันไม่ว่าผลลัพธ์ของ quota/credential จะเป็นอย่างไร ทำให้ history-only failure ไม่ทำให้ quota ที่
สำเร็จอยู่แล้วล้มเหลว และในทางกลับกัน quota/credential failure ก็ไม่ทำให้ history ที่อ่านได้ถูกซ่อน (PAR-004)
— ต่อมา Phase 4E เพิ่ม `AIMonitor.Infrastructure.Providers.OpenAi` ทั้ง slice: `OpenAiCredentialResolver`
(ลำดับ credential ตาม PAR-008 — Codex ChatGPT OAuth ก่อนเสมอ ไม่ว่า Connected หรือ Expired, ตกไปที่ saved
Admin key → environment key → Codex CLI login เฉพาะเมื่อไม่มี Codex OAuth เลย), `CodexJsonRpcClient` (JSON-RPC
framing บน stdin/stdout ล้วน ทดสอบด้วย `TextWriter`/`TextReader` ในหน่วยความจำ), `CodexAppServerLauncher`
(subprocess จริงผ่าน injectable `ICodexProcess`, disposable temporary `CODEX_HOME` ต่อการเรียกหนึ่งครั้ง,
env sanitization ตัด secret-bearing prefixes ของ OpenAI/Codex/Anthropic/Claude/Gemini/Google, ส่ง access token ทาง stdin เท่านั้น — PAR-009) และ
`OpenAiLiveQuotaClient` (รวม Codex path กับ Admin API path ตาม PAR-010 — Admin key ที่ใช้ได้จริงอ่าน
spend/budget/history, ordinary project key เป็น Limited โดยไม่ยิง HTTP request, error message ไม่สะท้อน
response body กลับ) — แต่ยังไม่มี account enrichment หรือ UI ใด ๆ ต่อจากนี้ — ส่วนที่เหลือของ provider
รวมทั้ง Gemini credential discovery/Cloud Monitoring provider และ Application refresh orchestration แล้ว;
settings/secret storage กับ legacy migration foundation (Phase 5B) และ Windows startup/single-instance/geometry
retention (Phase 5C) implement และ verify ครบแล้ว; ส่วน tray host และ WPF composition root จะทำในเฟส UI ถัดไป

**หมายเหตุการตรวจสอบ Phase 4D:** ผ่านรอบ Writing Owner → independent Reviewer → Integrator แล้ว
reviewer พบและมี regression tests ปิดช่องว่างด้าน checkpoint เมื่อ cancel/I/O fail, การรายงานไฟล์ที่อ่านไม่ได้,
การไม่สร้าง history/$0.00 เมื่อ directory ใช้งานไม่ได้, token overflow และ concurrent history calls;
Integrator รัน full suite 445 tests, build, format verification และ NuGet vulnerability audit ผ่านทั้งหมด

**หมายเหตุการตรวจสอบ Phase 4E/4F/5A/5B/5C:** OpenAI/Codex และ Gemini ผ่าน Writing Owner → independent Reviewer →
Integrator แล้ว ข้อค้นพบด้าน pagination, response-shape/currency validation, subprocess I/O/cleanup,
credential precedence, incomplete Cloud Monitoring responses, int64 overflow, bounded history และ malformed
credential path มี regression tests/contract fixtures รองรับ; full suite 843 tests, build, format verification
และ NuGet vulnerability audit ผ่านทั้งหมด จึงยืนยัน PAR-008 ถึง PAR-012 เป็น Verified ต่อมา Phase 5A เพิ่ม
refresh orchestration และ Phase 5B เพิ่ม `AppSettings` schema, `JsonSettingsStore`, `DpapiSecretStore`,
read-only `LegacyRegistrySettingsReader` และ automatic one-time `MigrateLegacySettingsUseCase` พร้อม
atomic writes, bounded input, legacy provider/widget/geometry preservation และ synthetic DPAPI/Registry tests;
และ Phase 5C เพิ่ม `WindowsStartupRegistrar` (PAR-024), `NamedMutexSingleInstance` (PAR-023) และ `WindowGeometryManager` (PAR-021)
พร้อม suite รวม 891 tests ผ่าน 100% — การ wire เข้ากับ WPF startup/settings dialog/tray host จะทำในเฟส UI/composition root

## 1. System overview

AIMonitor 2.0 เป็นแอป Windows desktop ผู้ใช้เดียว (single-user, single-machine) ที่ติดตามการใช้งานและ
โควตาของบริการ AI สามตัว — Claude, OpenAI (Codex/ChatGPT และ API) และ Gemini — โดยอ่านข้อมูลจาก
credential/log ที่เครื่องมือ CLI ของแต่ละ provider สร้างไว้อยู่แล้ว หรือจาก API ของ provider นั้นโดยตรง
แล้วแสดงผลเป็น dashboard, compact widget และ system tray icon พร้อมแจ้งเตือนเมื่อใกล้เต็มโควตา
แอปไม่มี backend ของตัวเอง ไม่มี multi-tenant boundary และไม่ส่งข้อมูลผู้ใช้ออกไปยังบริการภายนอกใด ๆ
นอกจาก provider ที่ผู้ใช้เชื่อมต่อเองและการตรวจ GitHub release แบบ read-only

### Goals

- รักษาพฤติกรรมที่ผู้ใช้มองเห็นตาม `Docs/FEATURE_PARITY.md` (PAR-001 ถึง PAR-036) โดยไม่พึ่ง Python
  runtime
- ใช้ความสามารถ native ของ Windows/.NET ให้เหมาะสมขึ้น (DPAPI, named pipe, named mutex, WPF DPI/
  multi-monitor API) แทนการจำลอง Qt/Python แบบหนึ่งต่อหนึ่ง
- แยก business rule (parity, severity, pricing, ordering) ออกจาก UI framework เพื่อให้ทดสอบได้โดยไม่
  ต้องเปิด WPF runtime

### Non-goals

- ไม่เพิ่ม provider หรือฟีเจอร์ธุรกิจใหม่ที่ไม่มีในรุ่น Python เดิม
- ไม่สร้าง server-side component, ไม่มี public network API และไม่มี multi-user/multi-tenant model
- ไม่แก้หรือลบ source ของโปรเจกต์ Python เดิมที่ `D:\Dev\Apps\AIMonitor`
- ไม่ implement telemetry หรือส่ง usage data ไปยังบริการวิเคราะห์ภายนอกใด ๆ

## 2. Context diagram

```text
[ผู้ใช้ Windows]
      |
      v
[AIMonitor.Presentation.Wpf]  <-- Dashboard / Widget / Tray / Settings / Report UI
      |
      v
[AIMonitor.Application]  <-- use cases: refresh, build report, migrate settings, check update
      |
      v
[AIMonitor.Domain]  <-- severity, pricing, ordering, detection rules (ไม่มี dependency ขาออก)

[AIMonitor.Infrastructure] --implements ports ของ--> [AIMonitor.Application]
      |
      +--> Claude CLI transcripts (local filesystem, trust boundary: อ่านอย่างเดียว)
      +--> Codex App Server (local subprocess, stdio, trust boundary: process boundary ในเครื่อง)
      +--> OpenAI Admin API / API key (HTTPS, trust boundary: network ออกนอกเครื่อง)
      +--> Gemini Cloud Monitoring API + ADC/service account (HTTPS, trust boundary: network ออกนอกเครื่อง)
      +--> GitHub Releases API (HTTPS, read-only, trust boundary: network ออกนอกเครื่อง)
      +--> Windows Registry: HKCU\Software\AIUsageMonitor\AIUsageMonitor (legacy QSettings path,
           read-only import เท่านั้น รวม sub-group/nested key ทั้งหมดใต้ path นี้)
      +--> Windows Registry: HKCU ...\Run (startup entry ของรุ่นใหม่ อ่าน/เขียนได้)
      +--> %LOCALAPPDATA%\AIUsageMonitor\ (settings.json, secrets.dat ผ่าน DPAPI, logs)
```

Trust boundary หลัก: (1) เครื่องผู้ใช้ ↔ network ออกนอกเครื่องเมื่อเรียก provider API/GitHub, (2) DPAPI
`CurrentUser` scope ↔ Windows user account อื่นบนเครื่องเดียวกัน, (3) โปรเซสรุ่น C# ↔ โปรเซสรุ่น Python
เดิมที่อาจยังติดตั้งอยู่ระหว่าง upgrade — ขอบเขตนี้จัดการโดย**installer เป็นผู้หยุด process รุ่น Python
เดิมอย่างชัดเจน** ไม่ใช่ runtime detection ข้ามเวอร์ชัน เพราะรุ่น Python ไม่มี named mutex และใช้ Qt
local server name (`AIUsageMonitor|<username>|<scope>`) ซึ่งเป็นกลไกคนละชนิดกับ named mutex/pipe ของ
รุ่น C# — รุ่น C# ตรวจจับ instance ของรุ่น Python ไม่ได้ (ดู ADR-0003)

## 3. Repository map

```text
/
├── Docs/
│   ├── adr/                              # architecture decision records
│   ├── ARCHITECTURE.md
│   ├── TESTING.md
│   ├── AI_WORKFLOW.md
│   └── FEATURE_PARITY.md
├── src/
│   ├── AIMonitor.Domain/                 # entities, value objects, business rules — ไม่ผูก framework
│   ├── AIMonitor.Application/            # use cases, orchestration, port interfaces
│   ├── AIMonitor.Infrastructure/         # provider clients, settings/secret store, Windows integrations
│   └── AIMonitor.Presentation.Wpf/       # WPF app: Views, ViewModels, composition root, tray host
├── tests/
│   ├── AIMonitor.Domain.Tests/
│   ├── AIMonitor.Application.Tests/
│   ├── AIMonitor.Infrastructure.Tests/
│   ├── AIMonitor.Presentation.Wpf.Tests/
│   └── AIMonitor.TestSupport/            # fakes, fixture factories ที่ใช้ร่วมกันหลาย test project
├── installer/                             # Inno Setup script (ADR-0003) — ยังไม่สร้างในเฟสนี้
├── AIMonitor.sln                         # classic solution format
├── global.json                           # pin .NET SDK 10.0.401, rollForward: latestPatch
├── Directory.Build.props                 # nullable/implicit usings/warnings-as-errors ส่วนกลาง
├── AGENTS.md / CLAUDE.md / Requirements.md
```

`installer/` ยังไม่ถูกสร้างในเฟสนี้ (อยู่นอกขอบเขต Phase 3) ส่วนที่เหลือของโครงสร้างข้างต้นมีอยู่จริงใน
repository แล้ว หากมีการปรับ ให้แก้เอกสารนี้ให้ตรงกับ repository จริงทันที

## 4. Components and ownership

| Component | Responsibility | Public interface | Owner |
|---|---|---|---|
| `AIMonitor.Domain` | Value object และ business rule ล้วน: severity thresholds (PAR-016), pricing tier EXACT/ESTIMATED/UNKNOWN (PAR-007), ordering ของ quota window (PAR-005), detection status model (PAR-002) | Types และ pure function เท่านั้น ไม่มี I/O | Coding Owner |
| `AIMonitor.Application` | Use case orchestration: refresh provider, coalesce in-flight request, build report จาก snapshot ที่แสดงอยู่, one-time legacy migration, update check | Use case classes + port interfaces (`IUsageProviderClient`, `ISettingsStore`, `ISecretStore`, `IStartupRegistrar`, `ISingleInstanceCoordinator`, `IUpdateChecker`, `IClock`) | Coding Owner |
| `AIMonitor.Infrastructure` | Implement ports: HTTP/process client ต่อ provider, `ClaudeTranscriptStore` (read-only incremental JSONL ingestion ของ Claude CLI transcripts พร้อม checkpoint/de-duplication/retention — PAR-006), `OpenAiCredentialResolver` + `OpenAiLiveQuotaClient` (credential priority ตาม PAR-008, Admin API spend/budget/history ตาม PAR-010), `CodexJsonRpcClient` + `CodexAppServerLauncher` + `CodexEnvironmentSanitizer` + `CodexExecutableLocator` + `CodexQuotaMapper` (Codex App Server protocol ผ่าน disposable temporary `CODEX_HOME` — PAR-009), `JsonSettingsStore`, `DpapiSecretStore`, `LegacySettingsReader` (read-only), `WindowsStartupRegistrar` (HKCU Run), `NamedMutexSingleInstance` + named pipe activation server (scope ACL ผูกกับ current user SID เท่านั้น — ดู ADR-0003), `TrayIconHost` (`System.Windows.Forms.NotifyIcon` โดยตรง ไม่ผ่าน `WindowsFormsIntegration`), `RollingFileLogger`, `GitHubReleaseChecker` | Adapter class ต่อ port หนึ่งชนิด | Coding Owner |
| `AIMonitor.Presentation.Wpf` | Composition root (DI wiring), MainWindow (dashboard, PAR-014/PAR-018), WidgetWindow (compact mode, PAR-019/PAR-020), SettingsDialog (PAR-026/PAR-027), LogDialog/ReportViewer (PAR-030/PAR-031), AboutDialog/ReadmeDialog (PAR-034), custom-drawn gauge/chart controls (ADR-0001) | ViewModel public property/command ต่อ View | Coding Owner |

## 5. Dependency rules

- `Presentation.Wpf` เรียก `Application` ผ่าน ViewModel/use case เท่านั้น ไม่เข้าถึง `Infrastructure`
  โดยตรง ยกเว้น **composition root** (`App.xaml.cs`) ที่ต้องอ้างถึง `Infrastructure` เพื่อลงทะเบียน DI
  เท่านั้น ห้ามเรียก business logic ของ `Infrastructure` จากที่อื่นใน `Presentation.Wpf`
- `Application` orchestrate use case; business rule ที่มีผลต่อความถูกต้อง (severity, pricing, ordering)
  ต้องอยู่ใน `Domain` ไม่ใช่กระจายอยู่ใน use case หรือ ViewModel
- `Domain` ต้องไม่ import WPF, `System.Net.Http`, `Microsoft.Win32` (registry), filesystem API หรือ
  package ภายนอกใด ๆ นอกจาก .NET base class library ที่ไม่มีผลข้างเคียง
- `Infrastructure` implement interface ที่ `Application` ประกาศไว้ (`Application` ไม่ reference
  `Infrastructure` ไม่ว่ากรณีใด — dependency inversion ทางเดียว)
- ห้ามสร้าง circular dependency ระหว่าง project ใด ๆ ข้างต้น
- `AIMonitor.TestSupport` (fakes/fixture factories) reference ได้เฉพาะ `Domain`/`Application` interface
  ไม่ reference `Infrastructure` จริง เพื่อบังคับให้ unit/application test ใช้ fake เสมอ

## 6. Runtime data flow

### Application startup และ single-instance activation

1. Process เริ่มทำงาน → composition root ใน `Presentation.Wpf` สร้าง DI container โดยผูก
   `Infrastructure` เข้ากับ port ของ `Application`
2. `ISingleInstanceCoordinator` (Infrastructure: per-user named mutex ที่มี ACL ผูกกับ current user SID)
   พยายามถือ mutex ที่ผูกกับ `AppId` เดียวกับ installer (ADR-0003) — mutex/pipe นี้ตรวจจับได้เฉพาะ
   instance อื่นของรุ่น C# เท่านั้น ไม่สามารถตรวจจับ process ของรุ่น Python เดิมได้ (รุ่น Python ไม่มี
   named mutex; การเปลี่ยนผ่านจาก Python เป็นหน้าที่ของ installer — ดู ADR-0003)
   - ถือสำเร็จ → เป็น primary instance, เปิด named pipe server (current-user-only) รอรับคำสั่ง activate
     จาก instance ใหม่
   - ถือไม่สำเร็จเพราะมี instance เดิมอยู่แล้ว → ส่งคำสั่ง activate ผ่าน named pipe ไปยัง instance เดิม
     แล้ว process ใหม่ออกทันทีโดยไม่เปิดหน้าต่างซ้ำ
3. `ISettingsStore` โหลด `settings.json` จาก `%LOCALAPPDATA%\AIUsageMonitor\`
   - ไม่พบไฟล์ → เรียก one-time migration use case ที่ทำงานอัตโนมัติ อ่าน
     `HKCU\Software\AIUsageMonitor\AIUsageMonitor` (รวม nested key/sub-group ทั้งหมด) ผ่าน
     `LegacySettingsReader` (read-only) แล้วสร้าง `settings.json` ใหม่แบบ forward-only (ADR-0002)
4. `ISecretStore` (DPAPI `CurrentUser`) พร้อมใช้งานตามคำขอ sign-in ของแต่ละ provider — secret เดิมที่
   migrate ได้ (decrypt ด้วย DPAPI `CurrentUser` + known entropy เดิมสำเร็จ) จะถูก decrypt-in-memory
   แล้ว re-seal เข้า `secrets.dat` โดยอัตโนมัติในขั้นตอนเดียวกับ migration ของ settings; secret ที่
   migrate ไม่สำเร็จ (non-fatal ต่อ secret อื่นและต่อ Registry เดิม) ต้องให้ผู้ใช้ sign-in ใหม่ (ADR-0002)
5. `TrayIconHost` เริ่มแสดง icon; restore window geometry ของ dashboard หรือ widget mode ตาม
   display topology/DPI ปัจจุบันจาก layout ล่าสุดใน settings (สูงสุด 8 layouts, PAR-021)
6. เริ่ม background poller ต่อ provider ที่ผู้ใช้เชื่อมต่อไว้

Failure behavior:

- Legacy registry อ่านไม่ได้หรือไม่มีอยู่ → migration ใช้ default สำหรับ field นั้น, log เป็น
  information ไม่ใช่ error (ค่าจาก Registry เป็น best-effort ไม่ใช่ dependency บังคับ)
- `settings.json` เสีย/parse ไม่ได้ → สำรองไฟล์เดิมเป็น `settings.json.bak`, ใช้ default settings,
  บันทึก warning ผ่าน log ที่ผ่านการ redact แล้ว, แอปต้องยังเปิดใช้งานได้
- Named mutex อยู่ในสถานะ `AbandonedMutexException` (instance เดิม crash โดยไม่ปล่อย lock) →
  ถือ ownership ต่อได้ทันทีและ log warning ว่าพบ abandoned lock (PAR-023: crash ต้องไม่ทิ้ง stale lock)
- Named pipe เชื่อมต่อ instance เดิมไม่สำเร็จ (pipe หาย/timeout) → ปฏิบัติเหมือนไม่มี instance เดิม
  ถืออีกครั้งหนึ่งครั้ง หากยังล้มเหลวให้ exit พร้อม log แทนที่จะเปิดสอง instance พร้อมกัน

### Provider refresh

1. Timer หรือ manual "Refresh" trigger เรียก `RefreshProviderUseCase` ต่อ provider ที่เชื่อมต่ออยู่
   ผ่าน `Task` ที่รับ `CancellationToken` แยกจาก UI thread
2. แต่ละ provider ทำงานอิสระกัน: exception หรือ error response ของ provider หนึ่งถูกจับและแปลงเป็น
   `ProviderSnapshot` สถานะ error/limited/expired โดยไม่ยกเลิก provider อื่น (PAR-003)
3. ภายใน provider เดียวกัน quota/live fetch และ history fetch เป็นคนละ operation: หาก history fetch
   ล้มเหลว quota/live ที่สำเร็จต้องยังแสดงผล และ error ของ history ถูกแนบไว้ใน snapshot โดยไม่ยกระดับ
   เป็น provider failure ทั้งหมด (PAR-004)
4. หากมี refresh request ใหม่เข้ามาระหว่างที่อีกคำขอกำลังทำงานอยู่ ระบบเก็บไว้เพียง**คำขอ queued
   ล่าสุด**เท่านั้น (coalesce) แล้วปล่อยคำขอที่ค้างเก่ากว่าทิ้ง
5. ผลลัพธ์ถูก marshal กลับ UI thread ผ่าน `Dispatcher` เพื่ออัปเดต ViewModel/tray/widget

Failure behavior:

- Provider payload parse ไม่ได้ (รูปแบบเปลี่ยน/field หาย) → mapped เป็นสถานะ error พร้อมเหตุผลที่
  ตรวจสอบได้ ไม่ throw ขึ้นไปถึง UI layer
- Authentication/detection ล้มเหลว (token หมดอายุ, credential ไม่ครบ) → สถานะเป็น `Expired` หรือ
  `Not connected` พร้อม source ของ credential และเหตุผล ตาม PAR-002; **ห้าม fallback ไป credential
  แหล่งอื่นแบบเงียบ** สำหรับ OpenAI selection order (PAR-008)
- Network timeout → ไม่มี automatic retry ในเวอร์ชันนี้ (คงพฤติกรรมเดิม); ผู้ใช้ refresh ใหม่ได้ทันที
  เพราะ refresh คำสั่งใหม่ถูก coalesce ไม่ block กันเอง
- Partial failure ระหว่าง fetch หลาย field ของ provider เดียวกัน → เก็บเฉพาะ field ที่สำเร็จ, ไม่เติม
  ค่าอนุมาน/ศูนย์แทนข้อมูลที่ไม่มี (no-invented-data, `Docs/FEATURE_PARITY.md` PAR-013)

### Settings save และ live preview

1. เปิด `SettingsDialog` → โหลดสำเนา settings ปัจจุบันมาแก้ใน ViewModel โดยไม่แตะไฟล์จริง
2. เปลี่ยนค่า theme/opacity/topmost/window size → ส่งผลลัพธ์ preview ไปยัง MainWindow/WidgetWindow
   ทันทีผ่าน in-memory event ก่อนกด Save (PAR-027)
3. กด **Cancel** → คืนค่าที่แสดงผลกลับเป็นค่าก่อนเปิด dialog ทั้งหมด ไม่เขียน `settings.json`
4. กด **Save** → `ISettingsStore` เขียนไฟล์ใหม่แบบ write-temp-then-replace; startup entry
   (`IStartupRegistrar`) จะเปลี่ยนเฉพาะตอน Save เท่านั้น ไม่เปลี่ยนระหว่าง preview

Failure behavior:

- เขียน `settings.json` ไม่สำเร็จ (เช่น disk เต็ม/permission) → ไม่ทำลายไฟล์เดิม (เขียนไฟล์ temp ก่อน
  แล้วค่อย replace), แจ้งผู้ใช้ผ่าน dialog ว่า save ไม่สำเร็จ พร้อมเหตุผล
- เขียน startup entry ล้มเหลว (registry permission) → settings อื่นยัง save สำเร็จ, แจ้งเฉพาะส่วน
  startup ว่าไม่สำเร็จ ไม่ rollback ทั้งหมด

### Report/export

1. `BuildReportUseCase` พับ (fold) เฉพาะ snapshot ที่กำลังแสดงผลอยู่ในหน่วยความจำ **ไม่ fetch ใหม่**
   (PAR-030)
2. Section ของ provider ที่ error หรือไม่มี history ต้องปรากฏใน report พร้อมระบุเหตุผล ไม่ตัดออกเงียบ ๆ
3. Export เป็น CSV/Markdown/HTML: content ผ่าน escaping ตามรูปแบบไฟล์ปลายทาง, ชื่อไฟล์มีวันที่กำกับ
   (PAR-031)

Failure behavior:

- ไม่มี snapshot ให้ fold (ยังไม่เคย refresh สำเร็จ) → report ระบุว่าไม่มีข้อมูล ไม่สร้างค่าสมมติ

### Update check

1. `IUpdateChecker` เรียก GitHub Releases API แบบ read-only เพื่อเทียบเวอร์ชันปัจจุบันกับ release ล่าสุด
2. แยกกรณี private repository/rate-limit/offline ออกจากกัน และ fallback ไป highest tag เมื่อจำเป็น
   (PAR-033)
3. ไม่ดาวน์โหลดหรือติดตั้งไฟล์ใด ๆ โดยอัตโนมัติ — แสดงผลลัพธ์และลิงก์ให้ผู้ใช้ไปดำเนินการเองเท่านั้น

Failure behavior:

- เรียก GitHub ไม่สำเร็จ (network/rate-limit) → แสดงสถานะ "ตรวจสอบไม่สำเร็จ" พร้อมเหตุผล ไม่ throw
  ไปกระทบ flow อื่นของแอป

## 7. Data model and storage

| Entity/table | Purpose | Owner | Retention/notes |
|---|---|---|---|
| `settings.json` | Startup, tray, theme, window size, refresh interval, opacity, topmost, chart range/metric, window geometry (สูงสุด 8 layouts) | `AIMonitor.Infrastructure` (`JsonSettingsStore`) | คงอยู่จนผู้ใช้แก้ไข/ลบเอง หรือถอนการติดตั้งแบบลบ user data |
| `secrets.dat` | Saved credential (เช่น Admin API key) ที่ผู้ใช้เลือก save ไว้ | `AIMonitor.Infrastructure` (`DpapiSecretStore`) | ผูกกับ Windows user account ปัจจุบันเท่านั้น ถอดรหัสไม่ได้บน user/เครื่องอื่น; เขียนผ่าน write-temp-then-atomic-replace เช่นเดียวกับ `settings.json` (ADR-0002) |
| Rolling log files | Diagnostic log สำหรับ troubleshoot | `AIMonitor.Infrastructure` (`RollingFileLogger`) | จำกัดขนาด/จำนวนไฟล์ (size/count-bounded), ต้อง redact secret/payload ก่อนเขียนเสมอ |
| `HKCU\Software\AIUsageMonitor\AIUsageMonitor` (legacy) | Settings/secret ของรุ่น Python เดิม (QSettings organisation+application key) | รุ่น Python เดิม (read-only จากมุมมองรุ่น C#) | อ่านครั้งเดียวตอน migration เท่านั้น (settings และ secret ที่ decrypt สำเร็จ) ห้ามลบ/เขียนทับ |
| `HKCU ...\Run` (entry ของรุ่นใหม่) | Start-with-Windows ของรุ่น C# | `AIMonitor.Infrastructure` (`WindowsStartupRegistrar`) | หลัง initialization ครั้งแรก ค่าที่ผู้ใช้ตั้งใน Windows Startup Apps เป็น source of truth (PAR-024) |
| Provider-local files/CLI state (Claude CLI transcripts, Codex login, gcloud ADC) | External read-only input สำหรับ usage/credential | Tool ของแต่ละ provider (ภายนอกระบบนี้) | อ่านอย่างเดียว ไม่แก้/refresh token ของเครื่องมือภายนอก |

- Source of truth: `settings.json` สำหรับ settings รุ่นใหม่ (หลัง migration ครั้งแรก); legacy Registry
  เป็นแหล่งข้อมูล historical ที่ใช้ครั้งเดียว
- Identifier strategy: provider ระบุด้วย string enum คงที่ (`claude` / `openai` / `gemini`) ไม่มี
  database และไม่ต้องใช้ UUID/sequence
- Transaction boundary: หนึ่งไฟล์ `settings.json` ต่อการเขียนหนึ่งครั้ง เขียนผ่าน temp file แล้ว
  atomic replace เพื่อกัน partial write; ไม่มี multi-file transaction
- Migration tool/process: one-time, automatic, forward-only importer ใน `AIMonitor.Application` (ดู
  ADR-0002) รันอัตโนมัติเมื่อตรวจพบว่าไม่มี `settings.json` — ครอบคลุมทั้ง settings และ secret ที่
  decrypt ได้สำเร็จจาก legacy Registry
- Backup/restore: ไม่มี automated backup ของ user data; ไฟล์ settings ที่เสียจะถูกสำรองเป็น `.bak`
  ก่อนสร้างใหม่จาก default เพื่อการวินิจฉัยเท่านั้น ไม่ใช่ restore อัตโนมัติ

Schema changes ของ `settings.json` ต้องมี explicit `schemaVersion` field และ forward-compatible
migration ภายในไฟล์เดียวกัน แยกจาก legacy Registry migration path ที่ระบุใน ADR-0002

## 8. Interfaces

### Public API

- ไม่มี public network API — เป็น desktop app ผู้ใช้เดียว ไม่มี HTTP server ของตัวเอง
- Named pipe ระหว่าง instance ใช้สำหรับ single-instance activation เท่านั้น เป็น internal contract
  ภายในเครื่องเดียวกัน ไม่ใช่ public/versioned API

### Events and jobs

| Name | Producer | Consumer | Delivery/idempotency |
|---|---|---|---|
| Provider refresh tick | Timer ใน `Application` | `RefreshProviderUseCase` ต่อ provider | Coalesce เหลือ request ล่าสุดเมื่อมีคำขอค้างอยู่ (PAR-003) |
| Tray icon repaint | Tray poller (interval สั้นกว่า dashboard refresh) | `TrayIconHost` | Idempotent: ใช้ last-good reading ซ้ำได้เมื่อ refresh ล้มเหลว (PAR-022) |
| Single-instance activate | `ISingleInstanceCoordinator` (named pipe) | Primary instance's main window | At-most-one-effect: เรียกซ้ำเท่ากับ "นำหน้าต่างเดิมขึ้นหน้า" ซ้ำ ไม่มีผลข้างเคียงสะสม |
| Update check | Manual (About dialog) หรือ interval ที่ตั้งค่าได้ | `IUpdateChecker` | Read-only, เรียกซ้ำได้อย่างปลอดภัย (idempotent โดยธรรมชาติ) |

### External dependencies

| Dependency | Purpose | Timeout/retry | Failure impact |
|---|---|---|---|
| Claude CLI transcripts (local filesystem) | อ่าน usage history แบบ incremental (PAR-006) | ไม่มี network timeout; อ่านไฟล์ล้มเหลวข้าม record เสียแล้วไปต่อ | History section ของ Claude แสดง error โดยไม่กระทบ quota/live |
| Codex App Server (local subprocess, stdio) | อ่าน quota และ available token history ผ่าน protocol ของ Codex CLI ปัจจุบัน | Timeout ระดับ process call เดียว; ไม่ retry อัตโนมัติ | Provider OpenAI (Codex path) แสดง error/`Not connected` พร้อมเหตุผล |
| OpenAI Admin API | Spend/budget/history เมื่อไม่มี Codex OAuth (PAR-010) | HTTPS timeout มาตรฐานของ HTTP client; ไม่ retry อัตโนมัติ | Provider OpenAI (API mode) แสดง error/`Limited` ตามสิทธิ์ของ key |
| Gemini Cloud Monitoring API + ADC/service account | Request counts ของ Gemini (PAR-011, PAR-012) | HTTPS timeout มาตรฐาน; ตรวจครบทุก credential candidate ก่อนสรุป `Limited` | Provider Gemini แสดง error/`Limited` พร้อม source ของ credential ที่ใช้ |
| GitHub Releases API | ตรวจเวอร์ชันล่าสุดแบบ read-only (PAR-033) | HTTPS timeout มาตรฐาน; ไม่ retry อัตโนมัติ | แสดง "ตรวจสอบไม่สำเร็จ" เท่านั้น ไม่กระทบการทำงานหลักของแอป |

สำหรับ Codex/OpenAI integration ให้ยึดเอกสารทางการปัจจุบันของ OpenAI เป็นหลักเมื่อ contract เปลี่ยน
**Codex App Server capability ที่ใช้ external token ผ่าน `chatgptAuthTokens`/access token ทาง stdin
เป็น experimental capability ของ OpenAI ณ ตอนเขียนเอกสารนี้ (version-sensitive dependency) — ต้อง
ทำเครื่องหมายว่า experimental ตั้งแต่เอกสารสถาปัตยกรรมนี้ ไม่ใช่รอจนเริ่ม implementation** ต้องมี
contract/capability test ที่ตรวจจับเมื่อ Codex App Server เปลี่ยน protocol หรือถอด capability นี้ออก
และต้องนิยาม failure behavior ที่ actionable ไว้ล่วงหน้า **ตามลำดับความสำคัญของ credential ใน PAR-008**:
หากมี Codex ChatGPT OAuth อยู่แต่หมดอายุ, ถูกปฏิเสธ หรือ experimental capability นี้หายไป/เปลี่ยน ระบบ
ต้องแสดง Codex error ที่ actionable (พร้อมเหตุผลที่ตรวจสอบได้) แก่ผู้ใช้ **ห้าม fallback ไป OpenAI Admin
API mode โดยอัตโนมัติหรือแบบเงียบ**; OpenAI Admin API mode (PAR-010) ใช้ได้เฉพาะกรณีที่ไม่มี Codex OAuth
source อยู่เลยเท่านั้น ไม่ใช่ fallback ของ Codex OAuth ที่มีอยู่แต่ใช้งานไม่ได้

## 9. Cross-cutting concerns

- Authentication: แอปไม่ implement ระบบ auth ของตัวเอง — อ่าน credential ของแต่ละ provider จากที่ที่
  เครื่องมือ CLI/SDK ของ provider นั้นเก็บไว้อยู่แล้วแบบ read-only (Claude CLI login, Codex OAuth ผ่าน
  App Server, OpenAI Admin/API key, Gemini service account/ADC)
- Authorization: ไม่มี multi-user authorization ภายในแอป; ขอบเขตการเข้าถึงข้อมูลผูกกับ Windows user
  account ปัจจุบันผ่าน DPAPI `CurrentUser` scope และ `%LOCALAPPDATA%` ของ user นั้น
- Configuration: ตั้งค่าได้ผ่าน `SettingsDialog` เท่านั้น เขียนลง `settings.json`; ไม่มี environment
  variable ที่ควบคุม runtime behavior ของแอปเอง (environment variable ของแต่ละ provider CLI ยังคง
  ใช้ได้ตามที่ provider นั้นกำหนดเอง เช่น service-account path)
- Secrets: DPAPI scope `CurrentUser` เท่านั้น ห้ามมี fallback เป็น plaintext ไม่ว่ากรณีใด (PAR-029,
  ADR-0002)
- Logging: rolling file log ที่จำกัดขนาด/จำนวนไฟล์ใต้ `%LOCALAPPDATA%\AIUsageMonitor\logs\`
  ทุก log entry ต้องผ่านการ redact token, API key, session identifier และ payload body ของ provider
  ก่อนเขียนเสมอ (mandatory, ไม่มี log level ใดยกเว้น)
- Metrics/tracing: ไม่มีการส่ง telemetry ไปยังบริการภายนอก; สัญญาณเดียวคือ local rolling log ที่ผู้ใช้
  เข้าถึงเองผ่าน Log/Report dialog
- Caching: เก็บ last-good `ProviderSnapshot` ต่อ provider ไว้ในหน่วยความจำระหว่าง session; tray icon
  ใช้ last-good reading ต่อเมื่อ refresh ล้มเหลว (PAR-022) ไม่มี disk cache เพิ่มเติมนอกจากไฟล์ history
  ที่ provider tool นั้นสร้างไว้เอง
- Rate limiting: แอปไม่มี inbound endpoint ให้ต้อง rate limit; outbound call ไปยัง provider ใช้ quota
  ของ provider นั้นเอง โดยแอปป้องกัน request ซ้อนกันด้วยกลไก coalesce ใน §6 ไม่ใช่ rate limiter แยก
- Localization/timezone: แสดงผลตาม locale/timezone ของ Windows ปัจจุบัน; การจัดกลุ่ม usage รายวันของ
  Claude ใช้ "local day" ตาม timezone เครื่อง (PAR-006) ไม่แปลงเป็น UTC ก่อนแสดงผล

## 10. Quality attributes

| Attribute | Target | How verified |
|---|---|---|
| Availability | ไม่ใช้บังคับแบบ service (desktop, ไม่มี SLA เครือข่าย); เป้าหมายคือ crash-free session | สังเกตระหว่าง manual smoke test ตาม `Docs/TESTING.md` §8 และตรวจ log ว่าไม่มี unhandled exception |
| Latency | UI thread ต้องไม่ block ระหว่าง refresh/report generation | Code review ยืนยันว่า provider I/O ทำงานนอก UI thread; สังเกต responsiveness ระหว่าง manual test |
| Throughput | ไม่ใช้บังคับ (ผู้ใช้เดียวต่อเครื่อง ไม่มี concurrent request จากภายนอก) | ไม่ต้องวัด |
| Data integrity | `settings.json`/`secrets.dat` ต้องไม่เสียหายจากการเขียนที่ถูกขัดจังหวะ (ทั้งคู่เขียนผ่าน write-temp-then-atomic-replace ตาม ADR-0002) | Integration test จำลอง interrupted write ของทั้ง `settings.json` และ `secrets.dat` และยืนยันว่าไฟล์เดิมยังอ่านได้ (`Docs/TESTING.md` §2) |
| Security/privacy | ไม่มี plaintext secret และไม่มี unredacted payload ใน log | Infrastructure test ยืนยันการใช้ DPAPI และยืนยันว่า log formatter redact token/secret เสมอ |

## 11. Architecture decisions

| ADR | Decision | Status |
|---|---|---|
| [0001-wpf-layered-mvvm-architecture](adr/0001-wpf-layered-mvvm-architecture.md) | WPF บน .NET 10 กับ layered architecture + MVVM, dependency direction Presentation → Application → Domain | Accepted |
| [0002-settings-secrets-and-legacy-migration](adr/0002-settings-secrets-and-legacy-migration.md) | Settings เป็น JSON ใต้ LocalAppData, secret ผ่าน DPAPI `CurrentUser`, migration จาก legacy Registry แบบ one-time/read-only | Accepted |
| [0003-packaging-and-in-place-upgrade](adr/0003-packaging-and-in-place-upgrade.md) | Packaging ด้วย Inno Setup self-contained win-x64, in-place upgrade ที่คง legacy `AppId` และป้องกัน duplicate startup/tray/poller | Accepted |

## 12. Known constraints and technical debt

- Feature parity ส่วนใหญ่ใน `Docs/FEATURE_PARITY.md` (PAR-001 ถึง PAR-036) ยังอยู่ในสถานะ `Not started`;
  PAR-003 ถึง PAR-012 และ PAR-016/PAR-017 เป็น `Verified` ส่วน PAR-001/PAR-002/PAR-026/PAR-029 ยังเป็น
  `In progress` ตามขอบเขตที่ implement จริง รายละเอียดและหลักฐานล่าสุดอยู่ใน `Docs/FEATURE_PARITY.md`
- WPF ไม่มี chart/gauge control สำเร็จรูปที่อนุมัติให้ใช้ ต้องเขียน custom-drawn control เอง
  (ADR-0001) ซึ่งเพิ่มต้นทุนบำรุงรักษาเทียบกับ chart library
- การอ้างอิง `System.Windows.Forms.NotifyIcon` โดยตรงจาก WPF app (ไม่ผ่าน `WindowsFormsIntegration`
  เพราะไม่ได้ host WinForms control ใด ๆ) เพิ่ม assembly reference ข้าม UI stack หนึ่งจุด (จำกัดผลกระทบ
  ไว้ที่ tray-hosting component ใน `Infrastructure`/`Presentation.Wpf` เท่านั้น)
- Minimum test coverage percentage ยังไม่ถูกกำหนดเป็นตัวเลขตายตัว (ดู `Docs/TESTING.md` §6) —
  เป็น open decision ที่ต้องยืนยันก่อนเริ่มพัฒนาโค้ดจริงหากทีมต้องการ threshold บังคับใน CI
- CI pipeline file (เช่น GitHub Actions workflow) ยังไม่ถูกสร้างในเฟสนี้ตาม constraint ของ Phase 3;
  `Docs/TESTING.md` §7 ระบุ gate ที่ pipeline ในอนาคตต้องบังคับใช้ไว้ล่วงหน้า
- `installer/AIMonitor.iss` (ADR-0003) ยังไม่ถูกสร้างในเฟสนี้ — อยู่นอกขอบเขต Phase 3
