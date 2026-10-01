# Testing

เอกสารนี้เป็นแหล่งข้อมูลหลักสำหรับวิธีตรวจสอบ **AIMonitor 2.0** (C# rewrite ของ AIMonitor บน .NET 10
สำหรับ Windows Desktop) คำสั่งด้านล่างอ้างอิงโครงสร้าง solution จริงตาม `Docs/ARCHITECTURE.md` §3 —
`AIMonitor.sln` และ `.csproj` ทุกตัวถูกสร้างแล้วใน Phase 3 หากมีการปรับโครงสร้าง project ให้ตรวจสอบว่า
path ในเอกสารนี้ยังตรงกับ project จริง แล้วแก้ไขตามทันทีที่ไม่ตรง

## 1. Quick start

```sh
dotnet restore AIMonitor.sln
dotnet build AIMonitor.sln --no-restore
dotnet test AIMonitor.sln --no-build
dotnet format AIMonitor.sln --verify-no-changes --no-restore
```

ข้อกำหนดเบื้องต้น:

- Runtime: .NET 10 SDK บน Windows (Windows Desktop targeting pack/runtime มาพร้อมกับชุด SDK/runtime ที่ติดตั้งอยู่แล้ว ไม่ต้องติดตั้ง workload แยก)
- Package manager: NuGet ผ่าน `dotnet` CLI (ไม่ใช้ package manager อื่น)
- Services ที่ต้องใช้: ไม่มี — ไม่มี database, ไม่มี container และไม่มี service ภายนอกที่ automated test
  ต้อง provision เอง (ทุก external dependency ถูกแทนด้วย fake ตาม §5)
- ตัวแปร environment: automated test ไม่ต้องใช้ environment variable ใด ๆ; ห้ามใช้ provider
  credential จริง (API key, OAuth token, service-account) ใน automated test ไม่ว่ากรณีใด

## 2. Test levels

| Level | Purpose | Location | Command |
|---|---|---|---|
| Unit | Business rule ล้วนใน `Domain` และ use case orchestration ใน `Application` แบบ isolated | `tests/AIMonitor.Domain.Tests/`, `tests/AIMonitor.Application.Tests/` | `dotnet test tests/AIMonitor.Domain.Tests` และ `dotnet test tests/AIMonitor.Application.Tests` |
| Integration | Adapter จริงของ `Infrastructure`: settings JSON read/write, DPAPI secret store, legacy registry import, single-instance/named pipe, HTTP/process client เทียบ fixture | `tests/AIMonitor.Infrastructure.Tests/` | `dotnet test tests/AIMonitor.Infrastructure.Tests` |
| Contract | Parsing ของ provider payload (Claude transcript, Codex App Server response — รวม capability ที่ยังเป็น experimental เช่น `chatgptAuthTokens`, OpenAI Admin API, Gemini Cloud Monitoring) เทียบ golden fixture ที่ไม่มี secret; Codex App Server contract test ต้องตรวจจับเมื่อ capability experimental หายไป/เปลี่ยน แล้วยืนยันว่าระบบแสดง Codex error ที่ actionable แทน silent failure และ**ไม่** fallback ไป OpenAI Admin API mode โดยอัตโนมัติเมื่อมี Codex OAuth อยู่แล้วแต่ใช้งานไม่ได้ (PAR-008); ต้องมี test แยกยืนยันว่า OpenAI Admin API mode (PAR-010) ทำงานเฉพาะกรณีไม่มี Codex OAuth source เลยเท่านั้น | `tests/AIMonitor.Infrastructure.Tests/Providers/`, fixtures ที่ `tests/Fixtures/` | `dotnet test tests/AIMonitor.Infrastructure.Tests --filter Category=Contract` |
| UI/component | ViewModel binding/command และ custom-drawn control logic (gauge/chart layout, widget paging) โดยไม่ต้อง render หน้าจอจริง | `tests/AIMonitor.Presentation.Wpf.Tests/` | `dotnet test tests/AIMonitor.Presentation.Wpf.Tests` |
| Smoke | ตรวจแอปจริงบน Windows หลัง build/installer | ดู §8 Manual verification | Manual (ยังไม่มี automated smoke script ในเฟสนี้) |

## 3. เลือกชุดตรวจสอบตามการเปลี่ยนแปลง

| Change | Required checks |
|---|---|
| Docs/comments only | ไม่ต้องรัน `dotnet test`; ตรวจ markdown link/format ถ้ามี tool ติดตั้ง |
| Domain logic (severity, pricing, ordering, detection rule) | `dotnet test tests/AIMonitor.Domain.Tests` แล้วตามด้วย `tests/AIMonitor.Application.Tests` เพราะ use case พึ่ง domain rule โดยตรง |
| Application use case/orchestration | `dotnet test tests/AIMonitor.Application.Tests`; ถ้ากระทบ port interface ให้รัน `tests/AIMonitor.Infrastructure.Tests` ด้วยเพราะ adapter ต้อง implement ports เดิม |
| Provider client/adapter (`Infrastructure`) | `dotnet test tests/AIMonitor.Infrastructure.Tests` รวม contract test ของ provider นั้น |
| Settings/secret store หรือ legacy migration (ADR-0002) | Integration test ของ settings/DPAPI/legacy reader ทั้งหมด (รวม nested key ใต้ `HKCU\Software\AIUsageMonitor\AIUsageMonitor`) + negative test ที่ยืนยันว่า legacy Registry (settings และ secret blob) ไม่ถูกเขียน/ลบ แม้ตอน migrate secret บางตัวล้มเหลว + test ยืนยัน atomic write ของ `secrets.dat` |
| Single-instance/startup/tray (Windows integration) | Integration test ที่เกี่ยวข้อง รวม negative test ว่า mutex/pipe จำกัดเฉพาะ current user SID (ADR-0003) + manual smoke checklist ข้อ single-instance/startup/tray ใน §8 |
| WPF View/ViewModel/widget behavior | `dotnet test tests/AIMonitor.Presentation.Wpf.Tests` + manual smoke ของ dashboard/widget mode ที่กระทบ |
| Packaging/installer (Inno Setup, ADR-0003) | `dotnet publish src/AIMonitor.Presentation.Wpf -c Release -r win-x64 --self-contained true` สำหรับ artifact ที่ installer ใช้จริง (ไม่ใช่ `dotnet build -c Release` เฉย ๆ เพราะ installer ต้องการ self-contained win-x64 publish output ตาม ADR-0003 ข้อ 2) + manual install/upgrade smoke ใน §8 (ยังไม่มี automated installer test ในเฟสนี้) |
| Dependency/NuGet package เปลี่ยน | `dotnet restore` + `dotnet build` + affected test suite ตามชั้นที่ dependency นั้นถูกใช้ |
| การเปลี่ยนที่กระทบมากกว่าหนึ่งชั้น (เช่น port interface ใหม่) | รัน full suite: `dotnet test` ที่ root ของ solution |

## 4. Test design rules

- Test พฤติกรรมที่สังเกตได้ (observable behavior) ไม่ผูกกับ implementation detail โดยไม่จำเป็น
- ทุก bug fix ต้องมี regression test ที่ fail ก่อน fix และ pass หลัง fix
- ครอบคลุม happy path, boundary (เช่น quota 0%/100%, ไม่มี history, credential หมดอายุพอดี), invalid
  input และ failure path ที่สำคัญตาม `Docs/FEATURE_PARITY.md`
- Tests ต้อง deterministic และทำซ้ำได้; ห้ามพึ่งลำดับการรัน, เวลาจริง, network จริง, filesystem จริงนอก
  temp directory ของ test เอง หรือ shared mutable state
- ใช้ fake clock (`IClock` fake), seeded random และ controlled fixture เมื่อต้องทดสอบเวลา/ความสุ่ม
  (เช่น countdown ของ quota reset, tray rotation interval)
- Mock/fake เฉพาะที่ system boundary (`Infrastructure` ports) เท่านั้น; ห้าม mock logic ของ `Domain`/
  `Application` ที่กำลังทดสอบ
- ใช้ **hand-written fake เป็นค่าเริ่มต้น** สำหรับ port interface (ไม่ใช้ mocking framework แบบ
  dynamic proxy) เพื่อให้ fake อธิบายพฤติกรรมของ dependency ได้ชัดเจนและตรวจสอบได้ง่ายใน code review
- Test name อธิบายเงื่อนไขและผลลัพธ์ที่คาดหวัง (เช่น
  `Refresh_WhenHistoryFetchFails_KeepsQuotaSnapshotVisible`)
- ห้าม test เรียก provider network จริงหรือ subprocess ของ Codex App Server จริงไม่ว่ากรณีใด —
  ใช้ fake `HttpMessageHandler`/fake process transport เสมอ

## 5. Test data and isolation

- ใช้ synthetic data เท่านั้น; ห้ามใช้ credential, token หรือ payload จาก account จริงไม่ว่ากรณีใด
- Local test ต้องใช้ disposable fixture เท่านั้น และห้ามเข้าถึง production หรือ real credentials
  (สอดคล้องกับ `AGENTS.md`/`CLAUDE.md`)
- Local test database: ไม่มี — แอปไม่มี database; state ที่ต้อง isolate คือ filesystem (`settings.json`,
  `secrets.dat`, logs) และ Windows registry เท่านั้น
- Filesystem: ทุก test ที่แตะ `ISettingsStore`/`ISecretStore`/`RollingFileLogger` ต้อง inject
  `IEnvironmentPaths` (หรือ path เทียบเท่า) ที่ชี้ไป temp directory เฉพาะของ test นั้น ไม่ใช้
  `%LOCALAPPDATA%` จริงของเครื่องที่รัน CI
- Registry: ทุก test ที่เกี่ยวกับ legacy migration หรือ startup entry ต้องใช้ fake/in-memory
  `IRegistryReader`/`IRegistryWriter` เท่านั้น ห้ามแตะ `HKCU` จริงระหว่าง automated test โดยไม่มี guard
  — legacy path จริงคือ `HKCU\Software\AIUsageMonitor\AIUsageMonitor` (organisation + application key
  ตาม QSettings เดิม) รวม nested key/sub-group ทั้งหมด; fixture ของ legacy migration test ต้องครอบคลุม
  โครงสร้าง nested key นี้ ไม่ใช่แค่ key เดี่ยวที่ organisation level
- Network/process: provider client ทุกตัวต้อง inject fake `HttpMessageHandler` หรือ fake process
  transport ที่คืนค่าจาก fixture ที่เตรียมไว้ล่วงหน้า
- แต่ละ test ต้องสร้างและล้าง temp directory/fixture ของตนเอง (xUnit `IDisposable`/
  `IAsyncLifetime`) โดยไม่พึ่ง global reset command
- Fixture factories และ fake ที่ใช้ร่วมกันหลาย test project เก็บที่ `tests/AIMonitor.TestSupport/`
- Golden fixture ของ provider payload (ไม่มี secret) เก็บที่ `tests/Fixtures/` และต้องถูก sanitize
  ก่อน commit เสมอ (ไม่มี token/PII ใด ๆ ปนอยู่)
- Test ที่ทดลอง interrupted/partial write หรือ abandoned mutex ต้องทำงานเฉพาะกับ temp resource ของ
  ตัวเอง (temp file, temp-named mutex ที่มี suffix ไม่ซ้ำต่อ test run) ห้ามแตะ resource ที่ตั้งชื่อตาม
  `AppId` จริงของแอประหว่าง automated test

## 6. Coverage policy

- Coverage เป็นสัญญาณ ไม่ใช่เป้าหมายเพียงอย่างเดียว
- Minimum coverage percentage ของทั้ง solution ยังไม่ถูกกำหนดเป็นตัวเลขตายตัวในเฟสนี้ — เป็น
  open decision (ดู `Docs/ARCHITECTURE.md` §12) ที่ Coding Owner ต้องยืนยันก่อนเปิด CI gate ที่บังคับ
  ตัวเลข; จนกว่าจะยืนยัน ให้ยึด rule ด้านล่างแทนตัวเลข
- Business rule ใน `Domain` ที่ผูกกับ parity ID ใน `Docs/FEATURE_PARITY.md` (เช่น PAR-005, PAR-007,
  PAR-016) ต้องมี unit test ครอบคลุมทุก decision path ก่อนถือว่า parity ID นั้น `Verified`
- โค้ดใหม่/เปลี่ยนใน `Application`/`Infrastructure` ต้องครอบคลุม happy path และ failure path ที่มีความ
  เสี่ยงต่อ data integrity หรือ credential safety อย่างน้อยหนึ่ง test ต่อ path
- ห้ามเพิ่ม assertion ที่ไม่มีความหมายเพียงเพื่อให้ตัวเลข coverage ผ่าน
- รายงานส่วนที่ไม่ทดสอบพร้อมเหตุผลและความเสี่ยงที่ยอมรับไว้ใน PR/handoff summary

## 7. CI quality gates

Pull request ต้องผ่านตามลำดับนี้ (pipeline file เช่น `.github/workflows/ci.yml` **ยังไม่ถูกสร้างใน
เฟสนี้** ตาม constraint ของ Phase 2 — รายการนี้คือ gate ที่ pipeline ในอนาคตต้อง encode):

1. `dotnet format --verify-no-changes`
2. `dotnet build AIMonitor.sln` พร้อม warnings-as-errors ตามที่ `Directory.Build.props` กำหนดจากส่วนกลาง
3. `dotnet test tests/AIMonitor.Domain.Tests` และ `dotnet test tests/AIMonitor.Application.Tests`
4. `dotnet test tests/AIMonitor.Infrastructure.Tests` (รวม contract test ของ provider)
5. `dotnet test tests/AIMonitor.Presentation.Wpf.Tests`
6. `dotnet publish src/AIMonitor.Presentation.Wpf -c Release -r win-x64 --self-contained true` เพื่อ
   ยืนยันว่า self-contained win-x64 publish output ที่ installer ใช้จริง (ตาม ADR-0003 ข้อ 2) สร้างได้
   สำเร็จ — `dotnet build -c Release` เพียงอย่างเดียวไม่พอเพราะไม่ได้ยืนยัน self-contained runtime
   ที่ Inno Setup ต้องใช้จริง
7. `dotnet list package --vulnerable --include-transitive` (dependency/security check)

CI configuration: ยังไม่มี path จริง — บันทึกไว้ที่นี่ว่าต้องสร้างเป็น GitHub Actions workflow (หรือ
เทียบเท่า) ที่รันบน Windows runner เนื่องจากมี WPF และ Windows-specific integration test

Flaky test ห้าม rerun จนเขียวแล้วละเลย ให้เก็บหลักฐาน เปิด issue และ quarantine เฉพาะเมื่อมีเจ้าของกับ
วันติดตามชัดเจน

## 8. Manual verification

ใช้ manual testing เมื่อ automation ไม่คุ้มค่าหรือครอบคลุมไม่ได้ (เช่น DPI จริงหลายจอ, tray icon
rendering, installer upgrade) และบันทึก:

- Environment/build ที่ทดสอบ (เวอร์ชัน Windows, DPI setting, จำนวนจอ)
- Preconditions และ test data (synthetic credential/fixture ที่ใช้)
- Steps
- Expected/actual result
- Screenshot/log ที่ผ่านการ redact ข้อมูลสำคัญแล้วเมื่อจำเป็น

Critical smoke checklist (อ้างอิง parity ID จาก `Docs/FEATURE_PARITY.md`):

- Dashboard mode เปิด/ย่อได้ถึง 300×220 โดยไม่เปลี่ยนเป็น widget อัตโนมัติ (PAR-018)
- Widget mode ลาก/resize ได้ไม่เกิน 300×300, always-on-top และ opacity ทำงาน, chevrons เปลี่ยน
  snapshot ทันทีโดยไม่ fetch ใหม่ (PAR-019, PAR-020)
- Geometry คืนค่าเดิมต่อ display topology/DPI หลัง restart (PAR-021)
- System tray icon วาดจาก short-window usage และคง last-good reading เมื่อ refresh ล้มเหลว (PAR-022)
- เปิดสอง instance พร้อมกัน → instance ที่สองต้องเรียกหน้าต่างเดิมขึ้นมาแล้วออก ไม่เปิดซ้อน (PAR-023)
- Start with Windows ทำงานตาม Windows Startup Apps เป็น source of truth หลัง initialization
  (PAR-024)
- Settings dialog: live preview ของ theme/opacity/topmost/window size, Cancel คืนค่าก่อนเปิด dialog
  ครบ, startup entry เปลี่ยนเฉพาะตอน Save (PAR-027)
- Export CSV/Markdown/HTML และ print preview ใช้ light palette ตามที่ตกลง (PAR-031)
- Update check แยกกรณี private/rate-limit/offline และ fallback ไป highest tag ได้ (PAR-033)
- Upgrade path: ติดตั้งรุ่น Python เดิม → รันจริง → อัปเกรดด้วย installer รุ่น C# → ยืนยันไม่มี
  duplicate tray icon/startup entry/poller และ legacy Registry values ยังอยู่ครบ (ADR-0003) — ทดสอบทั้ง
  สองกรณี: (a) start-with-Windows เปิดอยู่ในรุ่น Python เดิม → หลังอัปเกรดต้องเปิดอยู่และชี้ไป
  executable ของรุ่น C# และ (b) start-with-Windows ปิดอยู่ในรุ่น Python เดิม → หลังอัปเกรดต้องยังปิดอยู่
  (ไม่ถูก installer เปิดขึ้นมาเอง)
- Error handling และ recovery ที่สำคัญ: ตัด network ระหว่าง refresh ต้องไม่ทำให้แอป crash หรือค้าง UI
  thread

## 9. Failure handling

เมื่อ test fail:

1. เก็บ command, error และ environment ที่เกี่ยวข้อง
2. ยืนยันว่า reproduce ได้ด้วยคำสั่งเดิม
3. แยกว่าเกิดจาก change ปัจจุบัน, pre-existing failure หรือ environment (เช่น Windows runner ไม่มี
   Desktop workload ติดตั้ง)
4. แก้เฉพาะ failure ที่อยู่ใน scope ของงานนั้น; อย่าซ่อนด้วย skip หรือ assertion ที่อ่อนลง
5. รัน targeted test ซ้ำ แล้วรัน broader suite ตามความเสี่ยงตาม §3

หากไม่สามารถรัน test ได้ (เช่น environment ไม่มี .NET SDK/Windows Desktop workload ที่ต้องใช้) ให้รายงาน
เหตุผล สิ่งที่ตรวจแทน (เช่น ตรวจ path/คำสั่งให้สอดคล้องกันด้วยการอ่านเอกสาร) และความเสี่ยงที่เหลือ

## 10. Performance and security testing

- Performance: ไม่มี load test แบบ server เพราะเป็น desktop ผู้ใช้เดียว; ตรวจ regression ด้าน
  responsiveness ด้วยการสังเกต manual smoke ว่า UI thread ไม่ค้างระหว่าง refresh/report generation
  (ดู `Docs/ARCHITECTURE.md` §10); ใช้ `dotnet-trace`/Visual Studio profiler เฉพาะเมื่อสงสัย regression
  จริง ไม่ใช่ gate มาตรฐานทุก PR
- Baseline และ regression threshold: ยังไม่กำหนดตัวเลขเวลาตอบสนองตายตัวในเฟสนี้ — ใช้ "ไม่ block UI
  thread" เป็นเกณฑ์เชิงคุณภาพจนกว่าจะมี baseline จริงจาก solution ที่ build ได้
- Security scanning: `dotnet list package --vulnerable --include-transitive` ก่อน merge ทุกครั้งที่
  เปลี่ยน dependency
- Dependency audit command: เดียวกับ security scanning ด้านบน
- Secret/credential redaction เป็น mandatory gate เมื่อแก้ logging code — ต้องมี test ยืนยันว่า
  formatter redact token/API key/payload สำคัญก่อน merge การเปลี่ยนแปลงใด ๆ ที่แตะ logging
- ห้ามยิง load test หรือ security test ไปยัง provider account จริงหรือ production endpoint ใด ๆ
  โดยไม่ได้รับอนุญาตชัดเจน; ใช้ fake transport ตาม §5 เสมอ

## 11. Release verification

ก่อน release:

- CI gates ใน §7 ผ่านจาก commit ที่จะ release
- Migration test (legacy Registry → `settings.json`) และ upgrade-in-place test (ADR-0002, ADR-0003)
  ผ่าน รวมถึง negative test ที่ยืนยันว่าไม่มีการลบ/เขียนทับ legacy Registry values
- Installer (`installer/AIMonitor.iss` เมื่อสร้างแล้ว) build ได้แบบทำซ้ำได้ด้วย ISCC.exe จาก artifact
  ของ `dotnet publish src/AIMonitor.Presentation.Wpf -c Release -r win-x64 --self-contained true`
  เดียวกับที่ CI ตรวจใน §7 ข้อ 6 (ไม่ใช่ output ของ `dotnet build -c Release`)
- Manual smoke checklist ใน §8 ผ่านบน Windows environment ที่สะอาด (clean install) และบน environment
  ที่มีรุ่น Python เดิมติดตั้งอยู่ (upgrade path)
- Rollback/forward-fix plan: เก็บ installer ของเวอร์ชันก่อนหน้าไว้เพื่อ re-install ได้ทันทีหาก in-place
  upgrade มีปัญหา (legacy Registry ที่ไม่ถูกลบทำให้ rollback ไปรุ่น Python เดิมยังทำได้ในกรณีฉุกเฉิน)
- Monitoring/alert: ไม่ใช้บังคับ — แอปไม่มี server-side component ให้ monitor; สัญญาณหลังปล่อยคือ
  local rolling log และ error report ที่ผู้ใช้ส่งมาเอง
