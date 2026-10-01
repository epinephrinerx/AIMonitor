# AIMonitor 2.0 — Feature-parity baseline

เอกสารนี้เป็น baseline สำหรับ `R-001` เพื่อย้าย `D:\Dev\Apps\AIMonitor` จาก Python/PySide6 เป็น C# บน Windows โดยอิงพฤติกรรมที่ผู้ใช้มองเห็นและกฎความถูกต้อง ไม่ใช่การแปลโครงสร้างไฟล์เดิมแบบหนึ่งต่อหนึ่ง

สำรวจล่าสุด: 2026-09-27

## 1. ขอบเขตที่สำรวจ

- Application source: 39 ไฟล์ Python ประมาณ 10,497 บรรทัด
- Provider implementations: Claude, OpenAI และ Gemini
- Automated tests: 20 ไฟล์ ประมาณ 5,973 บรรทัด รวม 341 test cases
- Packaging: PyInstaller one-file/one-directory และ Inno Setup
- แหล่งข้อมูลหลัก: `README.md`, `GUIDELINES.md`, source ใน `ai_usage_monitor/` และ tests ใน `tests/`

โปรเจ็กต์ Python เป็น read-only reference สำหรับการย้ายครั้งนี้ ห้ามแก้หรือลบ source เดิม

## 2. Behavioral parity matrix

สถานะเริ่มต้นทุกข้อเป็น `Not started` และต้องเปลี่ยนเป็น `Verified` ด้วยหลักฐานการทดสอบใน C# solution

| ID | Area | พฤติกรรมที่ต้องรักษา | หลักฐานจากรุ่นเดิม | สถานะ |
|---|---|---|---|---|
| PAR-001 | Provider contract | ทุก provider คืน meters, stats, history, account, error และ detection ในรูปแบบกลาง โดยยอมให้ข้อมูลบางชนิดไม่มีค่าได้ | `providers/base.py` → Domain contract + Claude/OpenAI/Gemini live quota clients (C#) | Verified |
| PAR-002 | Detection | แสดง Connected, Limited, Expired หรือ Not connected พร้อม source และเหตุผลที่ตรวจสอบได้ | `detection.py`, Connections UI → Credential detection tests across all 3 providers (C#) | Verified |
| PAR-003 | Refresh | ดึงข้อมูลนอก UI thread, ยกเลิกได้, provider หนึ่งล้มไม่หยุด provider อื่น และเก็บเฉพาะคำขอ queued ล่าสุด | `worker.py`, `test_refresh_lifecycle.py` → `RefreshProvidersUseCase`/`LatestRefreshCoordinator` + cancellation/coalescing/race tests | Verified |
| PAR-004 | Partial success | quota/live values ที่สำเร็จต้องยังแสดงเมื่อ history ล้ม และ history error ต้องไม่ถูกยกระดับเป็น provider failure | `ProviderSnapshot`, `test_partial_success.py` → provider client partial-success tests + `RefreshProvidersUseCaseTests` | Verified |
| PAR-005 | Claude quota | แสดง session, weekly และ per-model windows ตามเปอร์เซ็นต์/เวลา reset ที่ server ส่งมา โดยเรียงตามชนิด ไม่เรียงตามความเต็ม | `api.py`, `test_limit_order.py` → `ClaudeQuotaParserTests` (C# contract fixtures) | Verified |
| PAR-006 | Claude history | อ่าน Claude CLI transcripts แบบ incremental, deduplicate, aggregate ตาม local day/model/project และข้าม record เสียโดยไม่หยุดทั้งไฟล์ | `usage_log.py`, `test_transcript_ingest.py` → `AIMonitor.Infrastructure.Providers.Claude.ClaudeTranscriptStore` + `ClaudeTranscriptStoreIngestTests`/`ClaudeTranscriptStoreFileTests`/`ClaudeTranscriptStoreFixtureTests`/`ClaudeTranscriptStoreTokenCountTests` (C#) | Verified |
| PAR-007 | Pricing | แยก EXACT, ESTIMATED และ UNKNOWN; เปิดเผยค่าประมาณ/โมเดลไม่ทราบราคา และไม่นับ UNKNOWN เป็นมูลค่า | `pricing.py`, `test_unpriced_models.py` → `AIMonitor.Domain.ClaudeModelPricing`/`UsageCounters`/`UsageValueCaveatFormatter` + `ClaudeModelPricingTests`/`UsageCountersTests`/`UsageValueCaveatFormatterTests` (C#) | Verified |
| PAR-008 | OpenAI selection | Codex ChatGPT OAuth มาก่อน saved Admin key, environment key และ CLI API key; OAuth ที่หมดอายุห้าม fallback เงียบ | `openai_provider.py`, `GUIDELINES.md` ส่วนที่ 3 → `AIMonitor.Infrastructure.Providers.OpenAi.OpenAiCredentialResolver` + selection/detection tests | Verified |
| PAR-009 | Codex protocol | อ่าน quota และ available token history ผ่าน Codex App Server; ใช้ temporary home, ส่ง access token ทาง stdin, ไม่ส่ง refresh token/config, ไม่ refresh login | `codex_usage.py` → `CodexJsonRpcClient`/`CodexAppServerLauncher`/`CodexEnvironmentSanitizer`/`CodexExecutableLocator`/`CodexQuotaMapper` + protocol/security tests | Verified |
| PAR-010 | OpenAI API mode | เมื่อไม่มี Codex OAuth ใช้ Admin API mode สำหรับ spend/budget/history; ordinary project keyแสดง Limited | `openai_provider.py`, `test_openai_usage.py` → `OpenAiLiveQuotaClient` + Admin pagination/data-integrity fixtures and tests | Verified |
| PAR-011 | Gemini | อ่าน request counts ผ่าน Cloud Monitoring และรองรับ Gemini CLI, environment service-account file, user-selected file และ gcloud ADC | `gemini_provider.py`, `providers/sources.py` → `AIMonitor.Infrastructure.Providers.Gemini` + 144 targeted tests | Verified |
| PAR-012 | Credential validity | service-account JSON ต้องเป็น object และมี fields ที่จำเป็น; ตรวจครบทุก candidate ก่อนสรุป Limited | `test_gcloud_adc.py` → Gemini credential readers/resolver/file tests | Verified |
| PAR-013 | No invented data | ไม่สร้าง denominator/percentage เอง ไม่เติมวันที่ไม่มีข้อมูลเป็นศูนย์ และใช้ server window duration แทนการสมมติ 5 ชั่วโมง/7 วัน | `README.md`, `GUIDELINES.md` → `DomainGuard`, `UsageCounters`, `UsageValueCaveatFormatter`, null percentage rules (C#) | Verified |
| PAR-014 | Dashboard | มี Connections page และ tab ต่อ provider พร้อม gauges, stats, daily chart, model/project breakdown และ error/setup states | `main_window.py`, `dashboard.py` → `MainWindow`, `ProviderTabViewModel`, `GaugeControl`, `DailyChartControl` + tests (C#) | Verified |
| PAR-015 | Metrics and ranges | รองรับ Total tokens, output tokens, equivalent value และช่วง 7/14/30/90 วันตามข้อมูลที่ provider มีจริง | `settings.py`, header controls → `ProviderSnapshotRequest` (passing configured range & metric) + `ProviderTabViewModel` dynamic history bar mapping (C#) | Verified |
| PAR-016 | Meter semantics | เกณฑ์ local 75% High และ 90% Critical รวมกับ server severity โดยเลือกค่าที่ร้ายแรงกว่า; แสดง glyph/ข้อความร่วมกับสี | `theme.py`, gauge/compact/tray code → `SeverityRulesTests`, `MeterTests` (C#) | Verified |
| PAR-017 | Reset display | แสดงทั้ง countdown และเวลานาฬิกาของ reset เมื่อมีข้อมูล | `formatting.py`, meter widgets → `ResetDisplayFormatterTests` (C#) | Verified |
| PAR-018 | Dashboard mode | หน้าต่างปกติ ย่อได้ถึง 300×220; การ resize ต้องไม่เปลี่ยนเป็น widget อัตโนมัติ | `main_window.py` → `MainWindow.xaml` (MinWidth 300, MinHeight 220) + separate `WidgetWindow` (C#) | Verified |
| PAR-019 | Widget mode | frameless, ลาก/resize ได้, ขนาดสูงสุด 300×300, always-on-top และ opacity; จำนวน meter ลดตามความกว้าง ไม่ใช่ความสูง | `widgets/compact.py` → `WidgetWindow`, `WidgetViewModel` + `WidgetViewModelTests` (C#) | Verified |
| PAR-020 | Widget navigation | หมุนบริการที่มีข้อมูลทุก 4 วินาที, pin ได้, chevrons เปลี่ยน snapshot ทันทีโดยไม่ fetch และใช้ได้แม้ rotation ปิด | `main_window.py` → `WidgetViewModel` (4s timer, pin toggle, carousel wrap-around) + `WidgetViewModelTests` (C#) | Verified |
| PAR-021 | Geometry | dashboard/widget จำตำแหน่งแยกตาม display topology/DPI, เก็บ 8 layouts ล่าสุด และ fit กลับเข้า visible screen | `main_window.py`, `test_geometry_retention.py` → `WindowPlacement`/`WindowGeometryManager` + `WindowGeometryManagerTests` (C#) | Verified |
| PAR-022 | System tray | icon วาดจาก short-window usage, หมุนบริการทุก 2 วินาที, รักษา last good reading เมื่อ refresh fail และมี live menu/tooltip | `tray.py` → `TrayIconHost` (GDI+ dynamic bitmap, 2s timer, severity colors, context menu) (C#) | Verified |
| PAR-023 | Single instance | หนึ่ง instance ต่อ Windows user; instance ใหม่เรียกหน้าต่างเดิมขึ้นมาแล้วออก โดย crash ไม่ทิ้ง stale lock | `single_instance.py` → `NamedMutexSingleInstance` + `NamedMutexSingleInstanceTests` (C#) | Verified |
| PAR-024 | Startup | Start with Windows ใช้ per-user startup setting; หลัง first launch ให้ค่า Windows เป็น source of truth และไม่เขียนคืนเมื่อผู้ใช้ปิดจาก Startup Apps | `startup.py`, settings behavior → `WindowsStartupRegistrar` + `WindowsStartupRegistrarTests` (C#) | Verified |
| PAR-025 | Close/exit | Close สามารถ minimize to tray ตาม setting; Exit จาก menu/tray ปิดจริงและยกเลิก background work | `main_window.py` → `MainWindow.xaml.cs` (minimize-to-tray handling), `App.xaml.cs` (OnExit cleanup) (C#) | Verified |
| PAR-026 | Settings | defaults และตัวเลือกเดิมสำหรับ startup, tray, theme, window size, refresh interval, opacity, topmost, chart range/metric | `settings.py`, `settings_dialog.py` → `SettingsDialog`, `SettingsViewModel` + `SettingsViewModelTests` (C#) | Verified |
| PAR-027 | Live preview | theme/opacity/topmost/window size preview ทันที; Cancel คืนค่าก่อนเปิด dialog; startup เปลี่ยนเมื่อ Save | `settings_dialog.py` → `SettingsViewModel` (live theme preview, cancel revert, save commit) + `SettingsViewModelTests` (C#) | Verified |
| PAR-028 | Theme | Follow Windows/Light/Dark และ re-check system theme; dashboard, widget และ tray ใช้ severity semantics ชุดเดียว | `theme.py` → `ThemeManager` (Windows registry theme detection, dynamic resources) + `ThemeManagerTests` (C#) | Verified |
| PAR-029 | Credential safety | อ่าน login ของเครื่องมืออื่นแบบ read-only, ไม่แก้/refresh; saved secrets ใช้ Windows user-bound protection และห้าม fallback เป็น plaintext | `credentials.py`, `secrets.py` → external credential tests + `DpapiSecretStore`/legacy re-seal tests (C#); composition wiring complete | Verified |
| PAR-030 | Reports | report มาจาก snapshots ที่แสดงอยู่ ไม่ fetch ใหม่; แสดง failed/no-history sections และไม่สร้างข้อมูลที่ไม่มี | `report.py`, `test_report.py` → `UsageReportGenerator` (build from LatestSnapshots, Markdown/CSV/HTML, no re-fetch) + `UsageReportGeneratorTests` (C#) | Verified |
| PAR-031 | Export/print | export CSV/Markdown/HTML, escape content, filename มีวันที่ และ print preview ใช้ light palette | `log_dialog.py`, `test_report.py` → `UsageLogDialog`/`UsageLogViewModel` (Save as CSV/MD/HTML, PrintDialog) + `UsageLogViewModelTests` (C#) | Verified |
| PAR-032 | Menus/shortcuts | คงคำสั่ง Refresh, Sign-in, Log, Print, Exit, startup, theme, widget, settings, About และ shortcuts ที่ใช้ได้ใน widget mode | README Menus → `MainWindow.xaml` InputBindings (F5, F1, Ctrl+L, Ctrl+P, Ctrl+W) + header & tray context menu (C#) | Verified |
| PAR-033 | Update check | ตรวจ GitHub release แบบ read-only, ไม่ดาวน์โหลด/ติดตั้ง, แยก private/rate-limit/offline และ fallback ไป highest tag | `about_dialog.py`, `version.py`, `test_version.py` → `GitHubVersionChecker` (/releases/latest + /tags fallback, semantic version, rate-limit recognition) + `GitHubVersionCheckerTests` (C#) | Verified |
| PAR-034 | Documents | เปิด README, GPL-3.0 license, third-party notices และ developer information จากตัวแอปได้ | `readme_dialog.py`, main window About menu → `AboutDialog`/`AboutViewModel` (developer info, GPL-3.0, README link) + `AboutViewModelTests` (C#) | Verified |
| PAR-035 | Versioning | app, executable metadata และ installer ใช้ชื่อ/เวอร์ชันจาก source of truth เดียว; installer identity ต้องรองรับ upgrade แทน side-by-side | version tests, Inno Setup config → `Directory.Build.props` (VersionPrefix=2.0.0), `AssemblyName=AIUsageMonitor`, `installer/AIMonitor2.iss` (new AppId distinct from 1.x) | Verified |
| PAR-036 | Resource lifecycle | ปิด subprocess, pipes, HTTP work และ background workers เมื่อยกเลิก/ออก; tray-idle ลด timers/กราฟที่ไม่จำเป็น | refresh lifecycle tests, README Resource use → `App.OnExit` (disposing timers, tray, HTTP clients, single instance mutex/pipes) + cancellation tokens (C#) | Verified |

## 3. Implementation ที่ไม่ต้องเหมือน Python

สิ่งต่อไปนี้เป็น implementation detail และควรออกแบบใหม่ใน C# แทนการจำลอง PySide6/Python:

- Qt signals, widgets, painting และ `QSettings`
- Python thread/event orchestration และ `urllib`
- PyInstaller specs และ Python virtual environment
- Qt local socket/registry wrappers
- Python dataclasses และ module layout

ความเข้ากันได้ที่ต้องรักษาคือ observable behavior, credential safety, persisted user intent และ error semantics ไม่ใช่ชื่อ class/function เดิม

## 4. Data and compatibility boundaries

- Login files ของ Claude, Codex และ Gemini เป็น external read-only inputs
- Registry/settings ของรุ่นเดิม (`HKCU\Software\AIUsageMonitor\AIUsageMonitor` รวม nested key) ต้องไม่
  ถูกลบหรือเขียนทับระหว่างการพัฒนา
- การ migrate settings/secrets เป็น **automatic (ไม่ต้อง opt-in), one-time, forward-only และ
  read-only** จาก legacy Registry — รันอัตโนมัติครั้งเดียวเฉพาะตอนที่ยังไม่มี `settings.json`/
  `secrets.dat` (field ใหม่ที่ยังไม่เคย migrate) และต้องมี tests ก่อนเปิดใช้จริง (การตัดสินใจนี้มาแทน
  ร่าง "opt-in" ก่อนหน้านี้ตามที่ผู้ใช้เลือกไว้ชัดเจนใน ADR-0002)
- ห้ามใช้ credentials จริงใน automated tests; fixtures ต้องเป็น synthetic และ disposable
- ห้ามให้ tests เรียก provider network จริงโดยปริยาย
- Source tree `D:\Dev\Apps\AIMonitor` และ installed production copy อยู่นอก write scope

## 5. Verification strategy for the C# rewrite

1. Port pure domain rules ก่อน: parsing, ordering, pricing, severity, formatting และ report folding
2. ใช้ golden fixtures ที่ไม่มี secret เพื่อเปรียบเทียบผล Python baseline กับ C# สำหรับ protocol payloads ที่สำคัญ
3. ทดสอบ adapters ที่ boundary ด้วย fake filesystem, fake clock, fake process/HTTP transport และ isolated registry scope
4. ทดสอบ Windows integrations แยกจาก UI: DPAPI, startup, single-instance, theme และ display geometry
5. ทำ UI/component tests สำหรับ widget sizing/navigation และ manual smoke สำหรับ tray/DPI/printing
6. ทุก regression test ใหม่ต้องพิสูจน์ว่า fail เมื่อย้อน production behavior ที่มันคุ้มครอง

## 6. Open decisions for Phase 2

- .NET target: ใช้ SDK ที่ติดตั้งอยู่หรือกำหนด LTS รุ่นใหม่พร้อม installation policy
- Desktop UI framework: WPF, WinUI 3 หรือทางเลือกอื่น โดยพิจารณา tray, custom drawing, DPI, accessibility, startup footprint และ installer
- Settings store และ migration policy จาก `HKCU\Software\AIUsageMonitor\AIUsageMonitor`
- HTTP/process abstraction และ JSON contract strategy สำหรับ provider payloads
- Packaging: self-contained/framework-dependent, architecture targets, signing และ installer technology
- Compatibility policy กับ installation รุ่น Python รวมถึง AppId, startup entry และ single-instance identity

## 7. Phase 1 exit criteria

- [x] ระบุ surface area และ dependencies ของรุ่นเดิม
- [x] ระบุ feature parity ที่ผู้ใช้มองเห็นและกฎ correctness/security
- [x] แยก behavior contract ออกจาก Python/PySide6 implementation detail
- [x] ระบุ Windows/data boundaries ที่ห้ามกระทบระหว่างพัฒนา
- [x] ระบุคำถาม architecture ที่ต้องตัดสินใน Phase 2
