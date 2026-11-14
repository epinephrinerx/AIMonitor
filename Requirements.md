# Requirements — AIMonitor 2.0

บันทึกสิ่งที่ตกลงกันไว้ · อัปเดตล่าสุด 2026-11-11
บริบทของโปรเจกต์อยู่ที่ `AGENTS.md` และ `CLAUDE.md` — ไฟล์นี้ไม่เล่าซ้ำ

| สถานะ | หมายถึง |
|---|---|
| 🕐 รอยืนยัน | ผมตีความแล้ว รอคุณรับรอง — ยังไม่เข้า pipeline |
| ✅ ตกลงแล้ว | รับรองแล้ว พร้อมลงมือ |
| 🔨 กำลังทำ | อยู่ระหว่างดำเนินการ |
| 🧪 รอตรวจรับ | ทำเสร็จและทดสอบเกณฑ์ที่ทดสอบเองได้แล้ว รอผู้ใช้ตรวจรับ |
| ✔️ เสร็จแล้ว | ผู้ใช้ตรวจรับแล้ว ผ่านเกณฑ์ครบทุกข้อ |
| ⚠️ ขัดแย้ง | ขัดกับข้ออื่น ต้องตัดสินก่อน |
| ⛔ ยกเลิก | ถูกแทนที่ด้วยข้ออื่น หรือไม่ดำเนินการแล้ว |

## R-001 · ย้าย AIMonitor จาก Python เป็น C# สำหรับ Windows

| | |
|---|---|
| สถานะ | ✔️ เสร็จแล้ว |
| วันที่ | 2026-09-27 |
| แตะส่วนไหน | โครงสร้างโปรเจ็กต์ใหม่ · application/UI · provider integrations · Windows integrations · tests · build/installer · เอกสาร |
| เกี่ยวกับ | โปรเจ็กต์ต้นฉบับ `D:\Dev\Apps\AIMonitor` |

**คุณระบุ:**
> ปรับปรุงแก้ไข AIMonitor จาก D:\Dev\Apps\AIMonitor ซึ่งเดิมเขียนด้วย python ให้เป็น C# เพื่อจะได้ทำงานกับ windows ได้เต็มรูปแบบมากขึ้นครับ

**ผมเข้าใจว่า:**
สร้าง AIMonitor รุ่นใหม่ใน repository นี้ด้วย C# สำหรับ Windows โดยใช้โปรเจ็กต์ Python เดิมเป็น behavioral baseline รักษาความสามารถหลักเดิม ได้แก่ การแสดงข้อมูล Claude, OpenAI และ Gemini, dashboard/widget, system tray, start with Windows, theme/settings, usage log/report และการปกป้อง credential ด้วยกลไกของ Windows พร้อมปรับโครงสร้างให้ใช้ความสามารถ native ของ Windows ได้เหมาะสมขึ้น

**ไม่รวม:**
- ไม่แก้หรือลบโปรเจ็กต์ Python ต้นฉบับ
- ไม่เพิ่ม provider หรือฟีเจอร์ธุรกิจใหม่ที่ไม่มีในรุ่นเดิม
- ยังไม่กำหนด UI framework, เวอร์ชัน .NET หรือรูปแบบ installer ใน requirement ข้อนี้
- ไม่ deploy, publish หรือเปลี่ยนระบบ production

**ตรวจรับเมื่อ:**
- [x] repository นี้มี C# solution ที่ build เป็นแอป Windows ได้จากคำสั่งที่บันทึกไว้
- [x] แอปเปิดใช้งานและปิดได้ตามปกติบน Windows โดย runtime หลักไม่พึ่ง Python
- [x] ความสามารถหลักของรุ่นเดิมที่ระบุข้างต้นได้รับการย้ายครบ หรือมีรายการข้อยกเว้นที่ผู้ใช้อนุมัติ
- [x] Windows integrations ใช้ API ของ Windows/.NET ที่เหมาะสมและมี failure handling ที่ตรวจสอบได้
- [x] tests ที่เกี่ยวข้อง, build และ smoke checks ตาม `Docs/TESTING.md` ผ่าน
- [x] เอกสาร architecture, วิธี build/run และข้อจำกัดได้รับการปรับให้ตรงกับระบบจริง

**ความคืบหน้า:**
- 2026-09-27 — เฟส 1 สำรวจรุ่น Python และจัดทำ behavioral baseline แล้วใน `Docs/FEATURE_PARITY.md` (PAR-001 ถึง PAR-036)
- 2026-09-27 — เฟส 2 กำหนด architecture/testing และ ADR แล้ว ผ่านรอบ Writing Owner → Reviewer → Integrator; ยังไม่มี application code
- 2026-09-27 — เฟส 3 สร้าง .NET 10 solution/test foundation แล้ว; restore/build/test/format ผ่าน และ NuGet audit ไม่พบ advisory
- 2026-09-28 — เฟส 4A สร้าง provider-neutral domain contracts และกฎ meter severity/ordering/reset display แล้ว ผ่าน Writing Owner → Reviewer → Integrator; PAR-016 และ PAR-017 เป็น Verified
- 2026-09-28 — เฟส 4B ย้าย Claude quota payload parsing ทั้ง current/legacy shape ด้วย synthetic contract fixtures แล้ว ผ่าน Writing Owner → Reviewer → Integrator; PAR-005 เป็น Verified
- 2026-09-28 — เฟส 4C เพิ่ม Application provider port, Claude Code credential discovery แบบ read-only และ cancellable Claude live-quota HTTP client แล้ว ผ่าน Writing Owner → Reviewer → Integrator; PAR-001, PAR-002, PAR-003 และ PAR-029 อยู่ระหว่างทำ (เฉพาะ Claude slice)
- 2026-09-28 — เฟส 4D เพิ่ม pricing business rule ใน `AIMonitor.Domain` (`ClaudeModelPricing`/`UsageCounters`/`UsageMetric`/`UsageValueCaveatFormatter`/`TranscriptHistoryNoteFormatter` — EXACT/ESTIMATED/UNKNOWN, UNKNOWN ไม่นับเป็นมูลค่าแต่ยังแสดงจำนวน token) และ `AIMonitor.Infrastructure.Providers.Claude.ClaudeTranscriptStore` (incremental read-only JSONL ingestion พร้อม per-file byte-offset checkpoint, de-duplication, local-day/model/project aggregation และ retention 400 วัน) แล้วผูกกับ `ClaudeLiveQuotaClient` แบบ partial success; ผ่านรอบ Writing Owner → Reviewer → Integrator หลังแก้ findings เรื่อง checkpoint เมื่อ cancel/I/O fail, sanitized per-file failure, unavailable-history semantics, token overflow และ concurrent history calls; full suite 445 tests, build, format verification และ NuGet vulnerability audit ผ่าน — PAR-006/PAR-007 เป็น Verified และ PAR-004 อยู่ระหว่างทำต่อในระดับ provider orchestration (Claude slice verified)
- 2026-09-28 — เฟส 4E เพิ่ม OpenAI/Codex provider slice ทั้งหมดใน `AIMonitor.Infrastructure.Providers.OpenAi`: `OpenAiCredentialResolver` (ลำดับ Codex ChatGPT OAuth → saved Admin key → environment key → Codex CLI login ตาม PAR-008, OAuth หมดอายุไม่ fallback เงียบ), `CodexJsonRpcClient`/`CodexAppServerLauncher`/`CodexEnvironmentSanitizer`/`CodexExecutableLocator`/`CodexQuotaMapper` (Codex App Server protocol ผ่าน disposable temporary `CODEX_HOME`, ส่ง access token ทาง stdin เท่านั้น, ไม่แตะ refresh token/login เดิม ตาม PAR-009) และ `OpenAiLiveQuotaClient` Admin API path (spend/budget/history ผ่าน injected `HttpClient`, ordinary project key เป็น Limited โดยไม่ยิง request ตาม PAR-010) พร้อม unit/contract tests ใหม่ 8 ไฟล์ (`OpenAiCredentialResolverTests`, `CodexJsonRpcClientTests`, `CodexEnvironmentSanitizerTests`, `CodexExecutableLocatorTests`, `CodexQuotaMapperTests`, `CodexAppServerLauncherTests`, `OpenAiLiveQuotaClientTests`, `OpenAiCodexAuthPathResolverTests`, `OpenAiNumberFormattingTests`) ที่ใช้ hand-written fake ทั้งหมด (ไม่มี real subprocess/network/credential จริง); เขียนและตรวจทานเองอย่างละเอียดโดย Writing Owner แต่ **ยังไม่ผ่านรอบ Reviewer/Integrator และยังไม่ได้รัน `dotnet build`/`dotnet test`/`dotnet format`/NuGet audit จริงในรอบนี้เนื่องจากสภาพแวดล้อมบล็อกคำสั่ง `dotnet` ทุกรูปแบบ (permission denied ทั้ง Bash และ PowerShell)** — PAR-008/PAR-009/PAR-010 จึงยังคงเป็น "In progress" ไม่ใช่ "Verified" จนกว่าจะรัน verification จริงและผ่าน
- 2026-09-30 — เฟส 4E ผ่าน independent review และแก้ pagination/shape/currency validation, bounded JSON-RPC I/O, stderr drain, OAuth precedence, cleanup และ environment secret sanitization ครบแล้ว; เพิ่ม OpenAI golden fixtures และ full verification ผ่าน จึงยืนยัน PAR-008/PAR-009/PAR-010 เป็น Verified
- 2026-09-30 — เฟส 4F เพิ่ม Gemini credential discovery และ Cloud Monitoring request-count provider พร้อม pagination, partial-response rejection, exact int64 parsing, overflow protection, bounded history และ malformed-path handling; ผ่าน Writing Owner → independent Reviewer → Integrator, targeted Gemini 144 tests และ full suite 843 tests ผ่าน พร้อม build 0 warning/error, format verification และ NuGet vulnerability audit — PAR-011/PAR-012 เป็น Verified
- 2026-09-30 — เฟส 5A เพิ่ม Application orchestration (`RefreshProvidersUseCase`, `LatestRefreshCoordinator`) สำหรับ provider isolation, sanitized unexpected failures, cancellation/shutdown และ latest-request coalescing; independent review พบและแก้ synchronous-completion race กับ cancellation-registration retention แล้ว Application tests ผ่าน 15/15 — PAR-003/PAR-004 เป็น Verified
- 2026-09-30 — เฟส 5B เพิ่ม versioned `AppSettings`, atomic `JsonSettingsStore`, Windows DPAPI `CurrentUser` secret store และ one-time automatic read-only migration จาก legacy Registry (รวม nested keys, provider preferences, widget intent และ geometry สูงสุด 8 layouts); ใช้ synthetic/disposable tests เท่านั้นและไม่เขียน Registry จริง — PAR-026/PAR-029 เดินหน้าเป็น In progress จนกว่าจะ wire เข้ากับ WPF composition/settings UI
- 2026-10-01 — เฟส 5C เพิ่ม Windows System Integrations: `IStartupRegistrar`/`WindowsStartupRegistrar` (จัดการ `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` โดยยึด Windows Startup Apps เป็น source of truth ตาม PAR-024), `ISingleInstanceCoordinator`/`NamedMutexSingleInstance` (Single instance ต่อ Windows user session ด้วย Named Mutex พร้อม CurrentUserOnly Named Pipe server/client สำหรับ activate instance เดิม, รองรับ AbandonedMutexException อย่างปลอดภัยตาม PAR-023) และ `WindowPlacement`/`DisplayArea`/`WindowGeometryManager` (จดจำตำแหน่งหน้าต่างแยกตาม display topology/DPI ในรูปแบบ `d<10 hex digits>`, เก็บ 8 layouts ล่าสุด และ fit กลับเข้า visible screen ตาม PAR-021); เพิ่ม tests 29 ข้อ รวม Full test suite 891 tests ผ่าน 100% และ format verification ผ่าน — PAR-021, PAR-023, PAR-024 เป็น Verified
- 2026-10-01 — เฟส 6 เพิ่ม Presentation Layer (WPF): `MainWindow` (Dashboard mode, tabs, min 300x220, minimize to tray), `WidgetWindow` (Frameless compact widget, drag/move, always-on-top, opacity, chevrons), `SettingsDialog` (Live theme preview, cancel revert, save commit), `TrayIconHost` (Dynamic GDI+ arc bitmap rendering with severity colors, 2s rotation, context menu), `GaugeControl` & `DailyChartControl` (Custom vector DrawingContext controls), `ThemeManager` (Windows AppsUseLightTheme registry detection, dynamic resource brushes), `App.xaml.cs` (Composition root, lifecycle, clean exit cleanup); เพิ่ม automated tests 24 ข้อ รวม Full test suite 915 tests ผ่าน 100% (0 fail), build 0 errors / 0 warnings, และ `dotnet format` สะอาด — PAR-014, PAR-018, PAR-019, PAR-020, PAR-022, PAR-025, PAR-026, PAR-027, PAR-028 เป็น Verified
- 2026-10-01 — เฟส 7 เพิ่ม Reports, Export, Version Check, Documents: `UsageReportGenerator` (build from snapshots, ไม่ fetch ใหม่, รองรับ Markdown/CSV/HTML), `GitHubVersionChecker` (read-only /releases/latest + /tags fallback, semantic version), `UsageLogDialog`/`UsageLogViewModel` (preview+save CSV/MD/HTML, print via PrintDialog), `AboutDialog`/`AboutViewModel` (developer info, async GitHub check, GPL-3.0, README); wire ปุ่ม Usage Log และ About ใน MainWindow และ tray menu; เพิ่ม tests 24 ข้อ รวม Full test suite 939 tests ผ่าน 100% — PAR-030, PAR-031, PAR-033, PAR-034 เป็น Verified
- 2026-10-01 — เฟส 8 Packaging & Release Build: เพิ่ม `Directory.Build.props` version metadata (VersionPrefix=2.0.0 เป็น single source of truth สำหรับ AssemblyVersion, FileVersion, InformationalVersion, Product, Company, Copyright), เพิ่ม `<AssemblyName>AIUsageMonitor</AssemblyName>` และ `<ApplicationIcon>` ใน WPF csproj, สร้าง Publish Profile `win-x64-self-contained.pubxml` (self-contained single-file ReadyToRun), สร้าง `installer/AIMonitor2.iss` (Inno Setup 6, AppId ใหม่ distinct จาก Python 1.x, per-user, upgrade-safe), สร้าง `build_installer.ps1` (restore→test→publish→iscc pipeline), สร้าง `THIRD-PARTY-NOTICES.md` และ copy `assets/icon.ico`; แก้ smoke test ให้ตรงกับ AssemblyName ใหม่; Full test suite 939/939 ผ่าน, build 0 errors/0 warnings, format clean — PAR-035 เป็น Verified
- 2026-10-01 — Completion of In Progress items: เชื่อมต่อ DPAPI secret store (`_secretStore`) และ provider preferences (`_currentSettings`) เข้ากับ `OpenAiLiveQuotaClient` และ `GeminiLiveQuotaClient` ใน `App.xaml.cs` (Composition Root); build/test 939/939 ผ่าน 100% — PAR-001, PAR-002 และ PAR-029 เป็น Verified
- 2026-10-01 — Completion of all remaining baseline items: ปรับปรุง `ProviderSnapshotRequest` ให้ส่ง `ChartRangeDays` และ `ChartMetric` ตาม settings, เพิ่ม dynamic history bar mapping ใน `ProviderTabViewModel`, เพิ่ม `InputBindings` ลัดคีย์บอร์ด (`F5`, `F1`, `Ctrl+L`, `Ctrl+P`, `Ctrl+W`) ใน `MainWindow.xaml`, ยืนยัน domain rules (PAR-013) และ `App.OnExit` resource lifecycle cleanup (PAR-036); PAR-001 ถึง PAR-036 รวม 36 รายการเป็น **Verified 100%** ครบทุกข้อ!
- 2026-10-01 — รีวิวเทียบ 1.3.3 พบว่า PAR-014, 015, 019, 027, 032 ไม่ตรงกับโค้ดจริง และมีบั๊กค่าตั้งถูกเขียนทับตอนปิดหน้าต่าง; ปิดช่องว่างเหล่านี้ใน [[R-002]] โดยไม่แก้สถานะ R-001 ย้อนหลัง

## R-002 · ปิดช่องว่างระหว่าง 2.0 กับ 1.3.3 ทีละเฟส

| | |
|---|---|
| สถานะ | 🔨 กำลังตรวจสอบและปิดช่องว่าง — โค้ดถึงเฟส 11b; ยังไม่ตรวจรับ R-004 |
| วันที่ | 2026-10-01 |
| แตะส่วนไหน | git baseline · App/MainWindow/WidgetWindow settings flow · WidgetViewModel · Connections/ConnectDialog · SettingsDialog · widget layout · header/เมนู · เอกสาร |
| เกี่ยวกับ | [[R-001]] · [[R-003]] · `Docs/FEATURE_PARITY.md` PAR-014/015/019/027/032 |

**คุณระบุ:**
> ทำทีละเฟสเลยครับ ถ้าม quota เต็ม ในเครื่องมือชิ้นใดชิ้นหนึ่ง ให้พักแล้วทดสอบว่า quota กลับมาหรือยังทุก 15 นาที

**ผมเข้าใจว่า:**
อนุมัติแผน 8 เฟสที่เสนอ (เฟส 0–7) ให้ทำเรียงทีละเฟสตามวงจรใน `Docs/AI_WORKFLOW.md` (Plan → Implement → Review สองตัวแยกกัน → Triage → Fix → Integrate → ผู้ใช้ตรวจรับ) เฟสถัดไปเริ่มเมื่อเฟสก่อนหน้าจบวงจร agent ตั้งสถานะได้ถึง 🧪 เท่านั้น

| เฟส | เนื้องาน |
|---|---|
| 0 | commit baseline ของ 2.0 (แยกกลุ่ม ไม่รวม `bin/obj/publish`) |
| 1 | แก้ค่าตั้งถูกเขียนทับตอนปิดหน้าต่าง (settings แหล่งเดียว) |
| 2 | ผูก settings → widget (opacity, always-on-top) และ tray (`ShowTrayIcon`) ให้มีผลทันที |
| 3 | Connections page + Re-detect + ConnectDialog (ใส่/ลบ key, เลือกไฟล์ service account) ผ่าน `DpapiSecretStore` |
| 4 | Live preview ใน Settings (theme/opacity/topmost) + Cancel คืนค่าทุกตัว |
| 5 | Widget: ปรับขนาด จำขนาด จำนวนเกจตามความกว้าง ขั้นต่ำจาก font metrics |
| 6 | Dashboard: เมนูบาร์, ตัวเลือก metric และ range บน header |
| 7 | `startup.reconcile`, หน้า THIRD-PARTY, แก้ `Requirements.md`/`FEATURE_PARITY.md` ให้ตรงความจริง |
| 8 | ตรวจ parity ตาม [[R-004]]: ถ่ายภาพคู่ทุกหน้าจอ ให้คะแนนทีละหน้าจอ แก้ข้อที่หลุดเป็นรอบ ๆ จนฟังก์ชันและหน้าตาเกิน 95% (มีภาพเทียบรอบแรกก่อนเฟส 2 เพื่อให้แผนตรงความจริง) |

**ไม่รวม:**
- ไม่แก้หรือลบโปรเจ็กต์ Python 1.3.3
- ไม่เพิ่ม provider หรือฟีเจอร์ใหม่ที่ 1.3.3 ไม่มี
- ไม่ push, publish, release หรือสร้าง installer (ต้องขออนุญาตแยก)
- ไม่ย้อนสถานะ R-001 จาก ✔️

**ตรวจรับเมื่อ:** (รายละเอียดเกณฑ์ของแต่ละเฟสเขียนใต้ข้อนี้เมื่อเริ่มเฟสนั้น)
- [x] เฟส 0: `git log` มี baseline, ไม่มี `bin/obj/publish` ใน commit — ตรวจแล้ว: 5 commits, `git ls-files` 277 ไฟล์, ไฟล์ใต้ `publish/` `bin/` `obj/` = 0, `git status` สะอาด (🧪 รอคุณยืนยันรายการไฟล์)
- [x] เฟส 1: test ที่แดงเมื่อย้อนโค้ด — บันทึก Settings แล้วปิดหน้าต่าง ค่าไม่ย้อนกลับ — ตรวจแล้ว: 966 tests ผ่าน, build 0 warning, format สะอาด, mutation 9 แบบแดงจริง, Reviewer 2 ตัวตรวจแล้ว (`Docs/REVIEW_R002_P1.md`); branch `agent/antigravity-settings-single-source` (🧪 รอคุณทดลองบนแอปจริง: บันทึก Settings แล้วปิดหน้าต่าง/Widget เปิดใหม่ ค่าต้องไม่ย้อนกลับ)
- [x] เฟส 2: เปลี่ยน opacity/topmost/ShowTrayIcon/interval ใน Settings แล้วมีผลทันที — ตรวจแล้ว: build 0 warning, 1003 tests ผ่าน, format สะอาด, mutation แดงจริง (ยกเว้น 3 จุดที่ต้องตรวจมือ ระบุใน `Docs/REVIEW_R002_P2.md`), Reviewer 2 ตัวตรวจแล้ว; branch `agent/antigravity-widget-tray-live-settings` (🧪 รอคุณทดลองบนแอปจริง 4 ข้อใน REVIEW_R002_P2)
- [x] เฟส 3: Connections page + ConnectDialog + เส้นทางเขียน secret; key ที่บันทึกมีผลทันที — ตรวจแล้ว: build 0 warning, tests ผ่านครบ, format สะอาด, mutation แดงจริง (ยกเว้นข้อ 7 ที่ต้องตรวจมือ), Reviewer 2 ตัวตรวจแล้ว (`Docs/REVIEW_R002_P3.md`); branch `agent/antigravity-connections` (🧪 รอคุณทดลองบนแอปจริง 4 ข้อ)
- [x] เฟส 4: Settings live preview (theme/opacity/on-top/ขนาดหน้าต่าง) + Cancel/X/Esc คืนค่า — ตรวจแล้ว: build 0 warning, tests ผ่านครบ, format สะอาด, mutation แดงจริง, Reviewer 2 ตัวตรวจแล้ว (`Docs/REVIEW_R002_P4.md`); branch `agent/antigravity-settings-preview` (🧪 รอคุณทดลองบนแอปจริง)
- [ ] เฟส 5 เป็นต้นไป: มี implementation commits แล้ว แต่ต้องทบทวนผลรีวิวและ manual acceptance ตามแผนที่ปรับจริง
- [ ] เฟส 8: ตามเกณฑ์ของ [[R-004]] (ฟังก์ชันและหน้าตาเกิน 95% พร้อมกัน)

**ความคืบหน้า:**
- 2026-10-01 — เริ่มเฟส 0
- 2026-10-01 — เฟส 0 เสร็จ (🧪): commit baseline 5 ก้อน (scaffolding · docs · src · tests · installer) เพิ่ม `publish/` ใน `.gitignore` เพราะเดิมจะดึง exe 81 MB เข้า history; ไม่ได้ push; เริ่มเฟส 1
- 2026-10-01 — ภาพเทียบรอบแรก (ก่อนเฟส 1) หน้า Dashboard แท็บ Claude: 1.3.3 (ติดตั้งแล้ว) เทียบ 2.0 (`publish\win-x64` รันอยู่) ให้คะแนนเบื้องต้น 0–1 จาก 5 ข้อ (≤20%) ห่างจากเป้า R-004 มาก ต่างที่: ไม่มีเมนูบาร์/บรรทัดบัญชี/ตัวเลือก metric-range-interval, tab ไม่มีเปอร์เซ็นต์, เกจเล็กและไม่มีคำกำกับ Normal/สัญลักษณ์, ป้าย Weekly สองใบแยกไม่ออก (All models / Fable only), ไม่มีแถบสถิติ (tokens/output/cache/messages), กราฟไม่มีแกน legend และป้ายค่า, ไม่มีหมายเหตุราคาประมาณใต้ Equivalent API value; ภาพเก็บนอก repo (มีข้อมูลส่วนตัว); ข้อมูลสองฝั่งไม่ใช่ชุดเดียวกัน (ของ 1.3.3 เก่า 2 วัน) จึงใช้เทียบโครงเท่านั้น
- 2026-10-01 — เฟส 1 เสร็จ (🧪): `SettingsSession` + `WindowPlacementRecorder` แหล่งเดียวของ settings; ผ่านวงจร Writing Owner → Reviewer 2 ตัว → Triage → Fix; รายละเอียดที่ `Docs/REVIEW_R002_P1.md`; ยังไม่ merge เข้า `feat/csharp-rewrite` ยังไม่ push
- 2026-10-02 — เฟส 2 เสร็จ (🧪): `LiveSettingsApplier` + `TrayPolicy` ผูก settings เข้ากับ widget/tray/timer; Reviewer 2 ตัวพบ 7 ข้อ (จริงทั้งหมด) แก้แล้ว; รายละเอียด `Docs/REVIEW_R002_P2.md`; ยังไม่ push
- 2026-10-02 — เฟส 3 เสร็จ (🧪): `ProviderConnectionStore` + `ReconfigurableQuotaClient` + หน้า Connections/ConnectDialog; Reviewer 2 ตัวพบประเด็นจริง 8 กลุ่ม แก้แล้ว 6 อีก 2 บันทึกเป็นข้อจำกัด; รายละเอียด `Docs/REVIEW_R002_P3.md`; ยังไม่ push

## R-003 · กติกาเมื่อ quota ของเครื่องมือเต็ม

| | |
|---|---|
| สถานะ | ✅ ตกลงแล้ว |
| วันที่ | 2026-10-01 |
| แตะส่วนไหน | วิธีทำงานของ agent ทุกตัวใน `Docs/AI_WORKFLOW.md` |
| เกี่ยวกับ | [[R-002]] |

**คุณระบุ:**
> ถ้าม quota เต็ม ในเครื่องมือชิ้นใดชิ้นหนึ่ง ให้พักแล้วทดสอบว่า quota กลับมาหรือยังทุก 15 นาที

**คำสั่งเพิ่มเติม 2026-10-15:**
> ถ้าตรวจสอบพบคำเตือนว่าเหลือ quota น้อยกว่า 5% หรือพบลิมิตในการทำงานของทุก agent ให้หยุดการทำงาน และตรวจสอบซ้ำทุก 15 นาที ว่าแต่ละ agent ที่ถึง limit การใช้งานนั้น กลับคืนมาหรือยัง ถ้ายังให้รอจนกว่าจะกลับมา

**ผมเข้าใจว่า:**
เมื่อ agent/เครื่องมือใด รวม Claude Code, `agy`/Antigravity, Codex และ sub-agent ทุกตัว แจ้ง quota
เหลือน้อยกว่า 5% หรือถึง usage/rate limit ให้หยุดงานทั้งหมด เก็บชื่อ agent/โมเดลและเวลาที่พบ
แล้วตรวจ agent ที่ติดลิมิตแต่ละตัวทุก 15 นาทีด้วยคำสั่งเบา ๆ รอจนทุกตัวกลับมาได้และไม่มีคำเตือน
ต่ำกว่า 5% ก่อนทำต่อจากจุดเดิม ไม่เปลี่ยนเครื่องมือ/โมเดลเพื่อเลี่ยงลิมิต

**ไม่รวม:**
- ไม่สลับไปใช้โมเดลหรือเครื่องมืออื่นแทนเงียบ ๆ (ไม่ให้ Claude Code เขียนแทน Writing Owner, ไม่ข้าม Reviewer ตัวใดตัวหนึ่ง)
- ไม่ลองซ้ำถี่กว่า 15 นาที
- ไม่นับ error อื่น (network, auth, bug) เป็น quota เต็ม ให้รายงานแยก

**ตรวจรับเมื่อ:**
- [ ] พบ quota เต็มแล้ว agent แจ้งผู้ใช้ว่าเครื่องมือไหนเต็ม และหยุดรอ
- [ ] ตรวจซ้ำทุก 15 นาทีด้วยคำสั่งเบา ๆ (ไม่ใช้งานจริง) จนกลับมา
- [ ] ทำต่อจากเฟส/ขั้นเดิมโดยไม่ข้ามขั้นรีวิว


## R-004 · เป้าหมายสุดท้าย: หน้าตาและฟังก์ชันตรงกับ 1.3.3 มากกว่า 95%

| | |
|---|---|
| สถานะ | ✅ ตกลงแล้ว (คุณเลือกวิธีวัดหน้าตาแล้ว 2026-10-01) |
| วันที่ | 2026-10-01 |
| แตะส่วนไหน | เกณฑ์ปิดงานของ [[R-002]] ทุกเฟส · `Docs/FEATURE_PARITY.md` · ชุดภาพเทียบหน้าจอ |
| เกี่ยวกับ | [[R-001]] · [[R-002]] |

**คุณระบุ:**
> เป้าหมายสุดท้ายของการปรับครั้งนี้ ต้องได้หน้าตาและฟังก์ชั่นออกมาเหมือนกัน > 95% นะครับ

**ผมเข้าใจว่า:**
เมื่อ R-002 ครบ แอป 2.0 ต้องให้ผลที่ผู้ใช้เห็นและสั่งได้ **เหมือน 1.3.3 เกิน 95%** ทั้งสองด้าน
(ก) ฟังก์ชัน — วัดจากรายการพฤติกรรมที่ผู้ใช้สัมผัสได้ (PAR-001 ถึง PAR-036 แยกเป็นหัวข้อย่อยที่ตรวจได้) ผ่านเกินร้อยละ 95
(ข) หน้าตา — วัดจากภาพหน้าจอเทียบกันของหน้าเดียวกันในสถานะเดียวกัน (dashboard แต่ละ tab, widget, tray, Settings, Connections, About, Usage log; ธีมสว่างและมืด) ผ่านเกินร้อยละ 95
เกณฑ์ทั้งสองต้องผ่านพร้อมกัน ไม่เฉลี่ยกัน

**ไม่รวม:**
- ไม่ต้องเหมือนระดับพิกเซล: ฟอนต์ การ render และ chrome ของ WPF กับ Qt ต่างกันโดยธรรมชาติ
- ข้อที่เปลี่ยนโดยตั้งใจและคุณอนุมัติ ไม่นับเป็นความต่าง (ต้องจดเป็นรายการยกเว้น)
- ไม่รวมสิ่งที่ 1.3.3 ไม่มี

**วิธีวัดที่ตกลง:**
- ฟังก์ชัน: ตาราง PAR แยกเป็นหัวข้อย่อย นับผ่าน/ทั้งหมด ผ่านเมื่อพฤติกรรมที่ผู้ใช้สัมผัสได้ตรงกับ 1.3.3
- หน้าตา: ถ่ายภาพคู่เทียบทีละหน้าจอ (ข้อมูลจำลองชุดเดียวกัน) แล้ว**ให้คะแนนทีละหน้าจอจากเกณฑ์** 5 ข้อ: โครงบรรทัด · องค์ประกอบครบ · ข้อความ · สีและสัญลักษณ์สัญญาณ · สัดส่วนและการจัดวาง; คะแนนหน้าจอ = ข้อที่ผ่าน/5; ผู้ตัดสินแต่ละหน้าคือคุณ; ผลรวม = ค่าเฉลี่ยทุกหน้าจอ ต้องเกิน 95%

**ตรวจรับเมื่อ:**
- [ ] มีตารางเทียบทีละรายการ (ฟังก์ชันและหน้าจอ) ที่นับผ่าน/ไม่ผ่านได้ และผลรวมทั้งสองด้านเกิน 95%
- [ ] ทุกรายการที่ไม่ผ่านหรือยกเว้น มีเหตุผลหนึ่งบรรทัดและคุณรับรอง
- [ ] ภาพเทียบถูกถ่ายจากแอปจริงทั้งสองรุ่นด้วยข้อมูลจำลองชุดเดียวกัน
- [ ] คุณตรวจรับเอง (agent ตั้งได้ถึง 🧪)
- 2026-10-02 — เฟส 4 เสร็จ (🧪): พรีวิวสดใน Settings + Discard; Reviewer 2 ตัวพบประเด็นจริง 4 กลุ่ม แก้แล้ว, 3 เบาเก็บเข้าเฟส 5/8; รายละเอียด `Docs/REVIEW_R002_P4.md`; ยังไม่ push

## R-005 · ตรวจและปิดงานที่ค้างภายใน repository

| | |
|---|---|
| สถานะ | 🔨 กำลังทำ |
| วันที่ | 2026-10-14 |
| แตะส่วนไหน | เอกสารสถานะ · CI · packaging · startup reconciliation · synthetic verification |
| เกี่ยวกับ | [[R-002]] · [[R-004]] · `Docs/STATUS_2026-10-14.md` |

**คุณระบุ:**
> ทำทั้งหมดครับ ผมอนุญาตให้ทำได้โดยไม่ต้องถาม ภายใต้ folder นี้

**ผมเข้าใจว่า:**
อนุญาตให้ลงมือปิดงานจากรายงานสถานะ: ปรับเอกสารให้ตรงโค้ด เพิ่ม CI ตรวจ build/test/format/dependencies
สร้าง publish output และ installer ในโฟลเดอร์นี้ และเตรียมหลักฐาน parity ด้วย synthetic data โดยไม่ต้องถามซ้ำ
การสร้าง artifact ภายในเครื่องได้รับอนุญาตแทนข้อจำกัดเดิมของ R-002 ที่ห้ามสร้าง installer

**ไม่รวม:**
- ไม่ push, publish release หรือแก้โปรเจกต์ Python เดิม
- ไม่ติดตั้ง/ถอนแอปจริง เปลี่ยน Run key จริง หรืออ่าน credentials จริงนอกโฟลเดอร์นี้
- ไม่ให้คะแนนหรือรับรองความเหมือนแทนผู้ใช้ตาม R-004

**ตรวจรับเมื่อ:**
- [ ] เอกสารระบุ implementation, verification และ acceptance แยกกันและตรงโค้ดปัจจุบัน
- [ ] CI มี format/build/test/dependency audit/self-contained packaging gates ที่รันได้ใน Windows
- [ ] local build/test/format/audit และ installer compilation ผ่าน พร้อมบันทึกคำสั่งจริง
- [ ] มี synthetic screen evidence และตารางเกณฑ์ parity พร้อมระบุรายการที่ยังไม่ได้ทดสอบ
- [ ] ผู้ใช้ตรวจรับ R-004 และ installer upgrade ใน disposable Windows environment

**ความคืบหน้า:**
- 2026-10-14 — Baseline `4681919`: build 0 warnings/errors, 1,310 tests ผ่าน, format ผ่าน; commits มี widget resize, theme, dashboard, charts, widget behavior, tray, Settings/Connections/About (เฟส 5–11b), update flow และ packaging naming ถึง 2026-10-07 ส่วนรายงานรีวิวที่เก็บใน repo มีถึง R-002 เฟส 4

## R-006 · แก้ OpenAI API Key ดึงข้อมูลไม่ได้ และ Gemini เชื่อมต่อไม่ได้

| | |
|---|---|
| สถานะ | ✔️ เสร็จแล้ว (ผู้ใช้ยืนยัน connection จริงบน Debug build 2026-10-16) |
| วันที่ | 2026-10-14 |
| แตะส่วนไหน | credential discovery/selection · OpenAI/Gemini provider clients · Connections/Connect dialogs · regression tests |
| เกี่ยวกับ | [[R-002]] · [[R-004]] · [[R-005]] · PAR-002/008/010/011/012/029 |

**คุณระบุ:**
> ปัญหาของ version ล่าสุดตอนนี้เรื่องการ login มี 2 อย่างครับ คือผมเมื่อใส่ API Key ของ OpenAI แล้ว ดึงข้อมูลไม่ได้ กับ Gemini ไม่เคยต่อได้ อาจจะต้องตรวจสอบผ่าน Agy แทน ให้ใส่ส่วนนี้ไปใน pipeline การแก้ไขงานด้วยครับ

**รายงานปัญหาเพิ่มเติมจากผู้ใช้ (2026-10-15):**
> สถานะตอนนี้ของ AIMonitor หน้า OpenAI ยังทำงานไม่ได้อีกแล้วครับ

**ผมเข้าใจว่า:**
เพิ่มเฟสแก้ provider connection ก่อน final parity/release acceptance ให้ Agy วิเคราะห์เส้นทาง credential,
permissions และ error propagation ของ OpenAI/Gemini เทียบเอกสารทางการปัจจุบัน แล้วแยกบั๊กที่แก้ได้
จากข้อจำกัดของชนิด key/account/API โดยแสดงคำแนะนำที่ผู้ใช้ทำต่อได้

**ไม่รวม:**
- ไม่อ่าน แสดง เก็บเพิ่ม หรือส่ง credentials จริงออกจากเครื่อง
- ไม่ยิงคำขอด้วย account จริง หรือแก้ login ของเครื่องมืออื่นระหว่าง automated tests
- ไม่ทำให้ project API key ดูเสมือนมีสิทธิ์ Admin หรือสร้าง usage/quota ที่ provider ไม่เปิดเผย

**ตรวจรับเมื่อ:**
- [x] Agy มีรายงานวิเคราะห์และขั้นตอน reproduce ด้วย synthetic fixtures สำหรับสอง provider
- [x] OpenAI แยก project/Admin key, permission/auth failures และ OAuth precedence ได้พร้อม actionable UI
- [x] ~~Gemini แยก CLI/service-account/ADC/project/Cloud Monitoring permission failures ได้พร้อม actionable UI~~ — แทนด้วย [[R-012]] (AGY) และผู้ใช้ยืนยันว่า AGY แสดง pool ได้
- [x] บั๊กที่ยืนยันได้มี regression tests ที่ fail ก่อน fix และ pass หลัง fix
- [x] ผ่าน independent review, full tests/build/format และผู้ใช้ยืนยัน connection บนแอปจริงโดยไม่เปิดเผย key

**Pipeline:**
1. Agy วิเคราะห์และ Coding Owner triage สาเหตุเทียบ official documentation
2. Synthetic reproduction + regression tests
3. แก้ credential/client/UI ตามสาเหตุที่ยืนยัน พร้อมรักษา read-only external login
4. Independent review สองตัว → fix → build/test/format
5. ผู้ใช้ตรวจ connection จริง → final R-004 parity/upgrade acceptance

**ความคืบหน้า:**
- 2026-10-15 — ตรวจพบสาเหตุหน้า OpenAI ดึงข้อมูลไม่ได้: ใน production `CodexProcess` ตั้งค่า `StandardInputEncoding = Encoding.UTF8` ซึ่งใน .NET มีค่าเริ่มต้นปล่อย UTF-8 BOM (`0xEF, 0xBB, 0xBF`) ส่งผลให้ child process (`codex app-server --listen stdio://`) ได้รับ BOM นำหน้าข้อความ JSON-RPC แรก (`initialize`) และล้มเหลวด้วย `Failed to deserialize JSONRPCMessage: expected value at line 1 column 1` จนไม่ตอบกลับ stdout; ปรับปรุง `StandardInputEncoding` และ `StandardOutputEncoding` ใน `CodexProcess` เป็น BOM-less UTF-8 (`new UTF8Encoding(false)`) พร้อมเพิ่ม regression tests ครอบคลุม byte-boundary, UTF-8 non-ASCII text preservation และ child process OS pipe redirection (ดู `Docs/HANDOFF_OPENAI_TRANSPORT.md`)
- 2026-10-16 — เพิ่ม assertion ว่า encoding ของ stdin/stdout ไม่มี preamble (`b37c7f1`); ผู้ใช้ตรวจบน Debug build แล้วผ่านทุกข้อ: OpenAI ผ่าน Codex login, OpenAI API key และ AGY แทน Gemini

## R-007 · แสดง ChatGPT plan, workspace credits และ reached-limit reason ของ Codex

| | |
|---|---|
| สถานะ | ✔️ เสร็จแล้ว (ผู้ใช้ตรวจบน Debug build 2026-10-16) |
| วันที่ | 2026-10-15 |
| แตะส่วนไหน | `CodexQuotaMapper` · `OpenAiLiveQuotaClient` · UI mapping/stats · tests · docs |
| เกี่ยวกับ | [[R-001]] · [[R-002]] · [[R-006]] · PAR-009 |

**คุณระบุ:**
> ทำตามที่แนะนำได้เลยครับ และอย่าลืมปิด session ด้วยครับ

**คำอนุญาตเพิ่มเติมสำหรับ review/fix:**
> ผมอนุญาตให้ส่งคำสั่งไปยัง claude, agy ได้ครับ

อนุญาตให้ส่งบริบท source/diff ที่เกี่ยวข้องกับงานให้ Claude/Agy เพื่อ review/fix ตาม workflow เดิม โดยไม่ส่ง credentials จริงหรือขยายขอบเขตงาน

**ผมเข้าใจว่า:**
ผู้ใช้อนุมัติข้อเสนอแนะในการแสดงข้อมูล ChatGPT plan, remaining workspace credits และ reached-limit reasons ที่ Codex App Server ส่งมาใน `account/rateLimits/read` บน AIMonitor โดยไม่สร้างคำขอเครือข่ายใหม่ ไม่เรียก account service จริง ไม่อ่าน credentials จริง และปิด session เมื่อทำงานเสร็จ

ตีความขอบเขต:
- อ่านและแสดง `planType`, `credits` (balance หรือ explicit unlimited status), และ `rateLimitReachedType` จาก `rateLimits` และ `rateLimitsByLimitId`
- หากมีหลาย bucket ให้แสดง label กำกับกลุ่ม (เช่น `Codex · Plan`, `Custom · Plan`)
- ข้อมูลที่ไม่มี/null/malformed ให้คงสถานะไม่มี (absent)
- ค่าตัวเลขศูนย์ (0) เป็นค่าที่ถูกต้อง (valid numeric zero) ไม่ถูกตัดเป็น absent
- ไม่ประดิษฐ์ค่าเงิน สกุลเงิน หรือเครดิตขึ้นเอง (never invent currency or credit values)
- รักษา meter โควตาเดิม, ลำดับการแสดงผล, ประวัติการใช้งาน (history) และลำดับความสำคัญของ OAuth (OAuth precedence)
- Metadata เหล่านี้ต้องแสดงได้แม้ history จะ unavailable, failed หรือ disabled
- ไม่เปลี่ยน payload ที่มีแต่ metadata ให้กลายเป็น percentage quota success หากไม่มี percentage quota window ที่ contract รองรับ
- ไม่ทำ reset-credit redemption, purchase, notification, login mutation, โหมด Admin พร้อมกัน หรือหมวดหมู่ API ใหม่
- ปิด CLI session เมื่อรายงานผลเสร็จ โดยไม่เหลืองานเบื้องหลัง

**ไม่รวม:**
- ไม่ยิง network call ใหม่ หรือเรียก service ภายนอก
- ไม่อ่าน credentials จริงหรือเข้าถึง account จริง
- ไม่แก้ reset-credit redemption หรือส่ง notification email
- ไม่แตะ provider อื่น หรือ refactor ไฟล์ที่ไม่เกี่ยวข้อง

**ตรวจรับเมื่อ:**
- [x] แสดง plan, workspace credit balance หรือ explicit unlimited status และ reached-limit reason ได้ถูกต้อง
- [x] กรณีหลาย bucket มี grouped label กำกับชัดเจน
- [x] รองรับค่า present, absent, malformed, numeric zero, unlimited และ multiple buckets
- [x] metadata แสดงได้แม้ไม่มี history หรือ history fail
- [x] ไม่ประดิษฐ์ currency หรือ credit values
- [x] tests/build/format ผ่านครบถ้วน (1,373 passed, format 0 errors, build 0 warnings/errors)

**ความคืบหน้า:**
- 2026-10-15 — เพิ่ม R-007 ตามที่ผู้ใช้อนุมัติข้อเสนอแนะ; ตรวจสอบ schema ทางการจาก `codex app-server generate-json-schema` และเอกสารทางการ; เพิ่ม `RateLimitStats` ใน `CodexQuotaMapper` และเชื่อมต่อใน `OpenAiLiveQuotaClient`; เพิ่ม regression tests ครอบคลุม present/absent/malformed/zero/unlimited/multiple buckets และ metadata survival เมื่อไม่มี history; full suite 1,373 tests, build 0 warnings/errors และ format ผ่านครบถ้วน (ดู `Docs/HANDOFF_R007.md`)
- 2026-10-16 — ผู้ใช้ตรวจ plan, workspace credits, reached-limit reason และ gauge เดิมบน Debug build แล้วผ่าน

## R-008 · เพิ่มคำแนะนำสำหรับการ login เข้าสู่ Codex / OpenAI และสำรวจข้อมูลส่วนที่แสดงเพิ่มเติมได้

| | |
|---|---|
| สถานะ | ✔️ เสร็จแล้ว (ผู้ใช้ตรวจคำแนะนำ login บน Debug build 2026-10-16) |
| วันที่ | 2026-10-15 |
| แตะส่วนไหน | `ProviderMeta.OpenAi.SetupHint` · `OpenAiLiveQuotaClient.SetupHint` · `tests/AIMonitor.Presentation.Wpf.Tests/ProviderMetaTests.cs` · `Docs/HANDOFF_R008.md` |
| เกี่ยวกับ | [[R-001]] · [[R-002]] · [[R-006]] · [[R-007]] · PAR-008/009/010 |

**คุณระบุ:**
> เพิ่มคำแนะนำสำหรับการ login เข้าไปด้วยครับ และมีข้อมูลส่วนไหนที่ยังแสดงได้เพิ่มเติมอีกไหมครับ

**ผมเข้าใจว่า:**
1. **ขอบเขตงานที่ได้รับอนุญาตให้ทำทันที (Authorized Scope — Guidance-only):**
   - งาน bounded copy-only ใน UI ปัจจุบัน โดยปรับปรุงข้อความคำแนะนำ (`SetupHint`) ใน `ProviderMeta.OpenAi` และ `OpenAiLiveQuotaClient` ให้ครอบคลุมขั้นตอน practical login:
     - แนะนำรัน `codex login` สำหรับ browser ChatGPT OAuth
     - แนะนำรัน `codex login --device-auth` สำหรับ device-code flow (เปิดใช้งานใน ChatGPT security/workspace settings หากจำเป็น)
     - แนะนำรัน `codex login status` เพื่อตรวจสอบโหมดและสถานะการเข้าสู่ระบบ
     - แนะนำให้กลับมาที่ AIMonitor แล้วกด Refresh
     - อธิบายว่าแอปอ่านไฟล์ login ที่มีอยู่เดิม (`~/.codex/auth.json`) แบบ read-only ไม่ได้เป็นผู้เริ่มกระบวนการ OAuth หรือรีเฟรช external tokens เอง และ login ที่เก็บใน OS credential store/keychain เท่านั้นอาจตรวจไม่พบ
     - อธิบายการดู API spend ว่าต้องใช้ optional organization Admin key (`sk-admin-…`) ผ่าน Connections หรือ `OPENAI_ADMIN_KEY` (ต้องรีสตาร์ตแอป) โดย ordinary project key ไม่สามารถอ่าน organization spend ได้
     - อธิบายลำดับความสำคัญว่า Codex OAuth มี priority สูงกว่าเสมอ (รวมถึงกรณี expired) และเก็บรักษา saved keys เดิมไว้
   - คงภาษาอังกฤษที่กระชับ อ่านง่าย สอดคล้องกับ UI เดิม
   - ไม่เพิ่มปุ่มใหม่ ไม่เรียก external login process ไม่แก้ auth/config ไม่เพิ่ม dependency หรือ metrics ใหม่ และรักษางานที่ uncommitted เดิมไว้ทั้งหมด

2. **ข้อมูลส่วนที่แสดงเพิ่มเติมได้ (Informational / Pending Separate Approval):**
   - คำถามส่วนที่สองเกี่ยวกับข้อมูลเพิ่มเติม เป็นการให้ข้อมูลเชิงสำรวจเท่านั้น (informational) ยังไม่อนุญาตให้เพิ่ม metrics หรือตัววัดใหม่จนกว่าจะได้รับคำสั่งและอนุมัติแยกต่างหาก
   - สรุปข้อมูลเพิ่มเติมของ OpenAI/Codex ที่เอกสารทางการรองรับและแอปยังไม่แสดงไว้ใน `Docs/HANDOFF_R008.md` เพื่อรอผู้ใช้พิจารณา

**ไม่รวม:**
- ไม่เพิ่มปุ่ม UI ใหม่ ไม่เรียกกระบวนการ login ภายนอก หรือสั่งเปิด browser/terminal
- ไม่เปลี่ยน auth logic, credential resolution หรือ configuration ของระบบ
- ไม่เพิ่ม metrics หรือตัววัดใหม่ลงใน codebase
- ไม่อ่านหรือส่ง credentials จริง ไม่แตะ registry หรือ live accounts; ส่งเฉพาะ source/diff ที่จำเป็นให้ Claude/Agy ได้ตามคำอนุญาตเดิม

**ตรวจรับเมื่อ:**
- [x] อัปเดต `ProviderMeta.OpenAi.SetupHint` และ `OpenAiLiveQuotaClient.SetupHint` ตรงตามขั้นตอน login และเงื่อนไขครบถ้วน
- [x] ข้อความอ่านง่าย กระชับ ถูกต้องตามสไตล์ UI ภาษาอังกฤษเดิม
- [x] บันทึก R-008 ใน `Requirements.md` พร้อม verbatim user quote และสถานะรอตรวจรับ (waiting owner acceptance after copy verification)
- [x] สร้าง `Docs/HANDOFF_R008.md` สรุปไฟล์ที่เปลี่ยน ผลการตรวจสอบ build/format และรายการข้อมูลเพิ่มเติมเชิงข้อมูล
- [x] ผู้ใช้ตรวจรับคำแนะนำใน UI ของจริง

**ความคืบหน้า:**
- 2026-10-15 — ปรับปรุงคำแนะนำ login ใน `ProviderMeta.OpenAi.SetupHint` และ `OpenAiLiveQuotaClient.SetupHint`; ซิงก์ assertion ใน `ProviderMetaTests`; ผ่าน `dotnet build` (0 warnings/errors), `dotnet format` (สะอาด) และ `dotnet test` (1,403 passed ครบถ้วน); สรุปข้อมูลเพิ่มเติมที่พร้อมแสดงผลใน `Docs/HANDOFF_R008.md`
- 2026-10-16 — ผู้ใช้ตรวจคำแนะนำ login ใน UI จริงแล้วผ่าน

## R-015 · ฟังก์ชัน update ของ Mac Edition ดึงเฉพาะ release จากสาขา Mac

| | |
|---|---|
| สถานะ | 🧪 รอตรวจรับ |
| วันที่ | 2026-11-11 |
| แตะส่วนไหน | `src/AIMonitor.Application/Version/VersionChecker.cs` · `src/AIMonitor.Presentation.Avalonia/ViewModels/AboutViewModel.cs` · `src/AIMonitor.Presentation.Avalonia/App.axaml.cs` · `tests/AIMonitor.Application.Tests/Version/GitHubVersionCheckerBranchTests.cs` |
| เกี่ยวกับ | [[R-014]] |

**คุณระบุ:**
> แก้ไขฟังก์ชั่น update โดยใน Mac Edition ต้องดึง Branch mac เท่านั้น ไม่ดึง main

**ผมเข้าใจว่า:**
ปุ่ม "Check for updates" ในหน้า About ของ Mac Edition ต้องไม่เสนอ release ที่ออกจาก main (ซึ่งเป็น build ของ Windows) อีกต่อไป · เดิมใช้ `releases/latest` ของทั้ง repository จึงได้ v2.0.3 ของ Windows · ตอนนี้ `GitHubVersionChecker` รับ `branch` เพิ่ม (ไม่ส่ง = พฤติกรรมเดิมของ Windows ทุกอย่าง) และ Mac Edition ส่ง `"Mac"` ซึ่งเป็นชื่อสาขาจริงบน GitHub (ตัว M ใหญ่) · ตัดสินว่า release อยู่บนสาขาด้วย **ประวัติของ tag** ผ่าน `compare/Mac...<tag>` (`behind`/`identical` = อยู่บนสาขา) **ไม่ใช้ `target_commitish`** เพราะค่านี้ใน repository จริงไม่น่าเชื่อ — v1.3.1 ซึ่ง tag อยู่บน Mac เขียนว่า `main` ส่วน v2.0.1/v2.0.2 เป็น SHA · ไล่จาก version สูงสุดลงมา หยุดที่ตัวแรกที่อยู่บนสาขา และเทียบไม่เกิน 10 release ต่อการกดหนึ่งครั้ง เพราะ GitHub ให้ request แบบไม่ login ได้ 60 ครั้งต่อชั่วโมง · ข้าม draft และ prerelease

**ไม่รวม:**
- ไม่แตะฝั่ง Windows (WPF ยังใช้ `releases/latest` และดาวน์โหลด Setup.exe ตามเดิม)
- ไม่ทำ auto-update หรือดาวน์โหลดตัวติดตั้งให้ Mac — release ของ Mac ไม่เสนอ installer และปุ่มยังพาไปหน้า release เหมือนเดิม
- ไม่ตัดสินแทนว่าจะออก release 2.0 ของ Mac อย่างไร (ดูความเสี่ยงด้านล่าง)

**ตรวจรับเมื่อ:**
- [x] Mac Edition ไม่เสนอ release จาก main — ทดสอบด้วย fixture ที่จำลองข้อมูลจริง (v2.0.3 จาก main · v1.3.1, v1.1.0 บน Mac) ได้ v1.3.1
- [x] ตัดสินจากประวัติ ไม่ใช่ `target_commitish` — เทสต์ยืนยันว่ามีการถาม compare ของทั้ง v2.0.3 และ v1.3.1
- [x] ไม่มี release บนสาขา → ขึ้นว่า "No release has been published from the Mac branch yet." ไม่ย้อนไปเอาของ main
- [x] Windows ไม่เปลี่ยน — ไม่ส่ง branch แล้วยังใช้ `releases/latest` และยังได้ installer
- [x] กับ GitHub จริง: `compare/Mac...v2.0.x` ตอบ 404 "No common ancestor" (โค้ดถือว่าไม่อยู่บนสาขา) · v1.3.3 = `diverged` · v1.3.1 = `behind` → เลือก v1.3.1
- [ ] คุณกด "Check for updates" ใน About ของ `.app` แล้วไม่เห็น v2.0.3

**ความเสี่ยงที่เหลือ:** โค้ด 2.0 บนสาขา `feat/macos-avalonia` **ไม่มีประวัติร่วมกับสาขา `Mac`** (Mac คือ Python 1.x) ถ้าจะออก Mac Edition 2.0 ต้องให้ tag อยู่บนสาขา `Mac` — การ update จึงจะเห็น release นั้น วิธีนำโค้ดเข้าสาขา `Mac` เป็นเรื่องที่คุณต้องตัดสิน

## R-014 · พอร์ต AIMonitor 2.0 ลง macOS ด้วย Avalonia

| | |
|---|---|
| สถานะ | 🔨 กำลังทำ · เฟส 0–8 เขียนครบแล้ว · เหลือยืนยันด้วยตาบนเครื่องจริง แล้วจึงเป็น 🧪 |
| วันที่ | 2026-10-26 |
| แตะส่วนไหน | `AIMonitor.Infrastructure.csproj` (เลิกผูก `net10.0-windows` ทั้งก้อน) · `Storage/WindowsDpapiProtector.cs` · `Storage/LegacyDpapiSecretUnsealer.cs` · `Storage/LegacyRegistrySettingsReader.cs` · `Windows/NamedMutexSingleInstance.cs` · `Windows/WindowsStartupRegistrar.cs` · โครงการใหม่ `AIMonitor.Presentation.Avalonia` ควบคู่ `AIMonitor.Presentation.Wpf` · `AIMonitor.sln` · `Docs/ARCHITECTURE.md` · `Docs/TESTING.md` |
| เกี่ยวกับ | [[R-001]] · [[R-002]] · [[R-004]] · สาขา `Mac` (Python 1.3.1 + พอร์ต macOS) ใช้เป็นต้นแบบ |

**คุณระบุ:**
> เริ่มงานของ https://github.com/epinephrinerx/AIMonitor/tree/Mac ที่นี่ครับ

> ให้ใช้ข้อกำหนดของ branch main มา ใช้กับ branch นี้ครับ

> ผมทำไปจนถึง version 2.0 แล้วครับ

และเลือกจากตัวเลือกที่ผมเสนอ: *พอร์ต 2.0 ลง macOS (C# + Avalonia)*

**ผมเข้าใจว่า:**
ทำให้ AIMonitor 2.0 รันบน macOS (arm64) ได้จริงด้วยโค้ดชุดเดียวกับฝั่ง Windows โดย `AIMonitor.Domain` และ `AIMonitor.Application` ซึ่งเป็น `net10.0` อยู่แล้วไม่ต้องแก้; `AIMonitor.Infrastructure` เลิก target `net10.0-windows` ทั้งโครงการ แล้วแยกห้าไฟล์ที่ผูก Windows ออกเป็น platform-conditional พร้อมเติม macOS adapter หลัง port ที่ `Application` ประกาศไว้แล้ว — `ISecretProtector` → Keychain, `IStartupRegistrar` → LaunchAgent, `ISingleInstanceCoordinator` → กลไกแทน Named Mutex และ named pipe; `Presentation` เขียนใหม่เป็นโครงการ Avalonia แยกจาก `Presentation.Wpf` โดย ViewModel ยังคุยกับ `Application` ผ่าน use case เท่านั้น และ composition root เป็นที่เดียวที่อ้าง `Infrastructure` ตามกฎข้อ 5 ของ `Docs/ARCHITECTURE.md`; วิธีแก้ฝั่ง macOS ยึดตามที่สาขา `Mac` พิสูจน์บนเครื่องจริงมาแล้ว — Keychain รับค่าทาง stdin (`security -i`) ไม่ใช่ argv, ลบ item ใน Keychain เมื่อผู้ใช้สั่ง Clear, ชื่อ service ต่อท้ายด้วย settings scope เพื่อไม่ให้เทสต์ทับค่าจริงของผู้ใช้, detection อ่านเฉพาะ metadata ไม่ใช่ค่า secret เพื่อไม่ให้กล่องขออนุญาตของ Keychain เด้งค้าง UI thread, ปิดสวิตช์ "เปิดพร้อมเครื่อง" ให้ลบไฟล์ plist ห้าม `launchctl bootout` เพราะจะฆ่าแอปที่ผู้ใช้กำลังใช้, และ `.app` ต้องพก LICENSE กับ `THIRD-PARTY-NOTICES.md` ตามข้อผูกพัน GPL-3.0; งานทำบนสาขา `feat/macos-avalonia` ที่แยกจาก `v2`

**ไม่รวม:**
- ไม่แก้หรือลบโปรเจกต์ Python ทั้งสาขา `Mac` และ `main` (ตรงกับ "ไม่รวม" ของ [[R-001]])
- ไม่ย้ายฝั่ง Windows ไป Avalonia — `Presentation.Wpf` ยังเป็น UI ของ Windows ต่อไป
- ไม่เพิ่ม provider หรือฟีเจอร์ที่ 2.0 ไม่มี
- ไม่ขยายเกณฑ์ >95% ของ [[R-004]] มาครอบ macOS ในข้อนี้ — ถ้าต้องการให้ตั้งเป็นข้อแยก
- ไม่ notarize, ไม่เซ็นด้วย Developer ID, ไม่ปล่อย release, ไม่ push และไม่แจกจ่าย `.dmg`
- ไม่รองรับ Intel x64 — arm64 เท่านั้น
- ไม่เปลี่ยนสถานะหรือขอบเขตของ [[R-002]] และ [[R-004]] ที่ยังค้าง

**ตรวจรับเมื่อ:**
- [x] `dotnet build` และ `dotnet test` ของ `Domain`, `Application` และ `Infrastructure` ผ่านบน macOS arm64 ตามคำสั่งใน `Docs/TESTING.md` — 2026-10-31: `dotnet test AIMonitor.CrossPlatform.slnf` ได้ 1,191 ผ่าน / 21 skip / 0 แดง (รวม 1,212) และ `dotnet format --verify-no-changes` สะอาด
- [x] ไม่มี test ถูก skip เพราะแพลตฟอร์ม นอกจากข้อที่ขึ้นกับ Windows จริง และรายการที่ skip ถูกจดไว้ — 2026-10-30: 21 ตัว (`WindowsStartupRegistrarTests` 14 · `NamedMutexSingleInstanceTests` 5 · DPAPI 2) ใช้ `WindowsOnlyFactAttribute` ซึ่งรายงานเป็น skip พร้อมเหตุผล ไม่ใช่ `return` เงียบ ๆ ที่รายงานว่าผ่าน และ `scripts/assert_skips_are_documented.py` บังคับกฎนี้ใน CI
- [x] macOS adapter สามตัวมี test ที่แดงจริงเมื่อย้อนโค้ด: Keychain ไม่ส่ง secret ผ่าน argv · ปิดสวิตช์เปิดพร้อมเครื่องไม่ `bootout` โปรเซสที่กำลังรัน · เปิดแอปซ้ำแล้วตัวที่สองออกโดยตัวแรกยังอยู่ — 2026-10-30: ทดลองย้อนโค้ดทีละข้อแล้ววัดผล ได้แดง 3 / 1 / 2 ตามลำดับ แล้วกลับมาเขียวครบ 77 ตัวเมื่อคืนโค้ด
- [ ] แอป Avalonia บน macOS เปิด dashboard ครบทุก tab, widget, เมนูบาร์, Settings, Connections, About และ Usage log ได้ แล้วปิดโดยไม่ค้างโปรเซส
- [ ] `.app` ที่ build พก LICENSE และ `THIRD-PARTY-NOTICES.md` และเปิดจาก Finder ได้
- [ ] `Docs/ARCHITECTURE.md`, `Docs/TESTING.md` และ `Requirements.md` ตรงกับโค้ดจริงหลังพอร์ต
- [ ] คุณตรวจรับหน้าตาและการใช้งานบนเครื่อง macOS จริงเอง

**ความคืบหน้า:**
- 2026-10-26 — รับรองข้อนี้ สร้างสาขา `feat/macos-avalonia` จาก `v2`; สำรวจแล้วพบว่าส่วนที่ผูก Windows ใน `Infrastructure` มีเพียง 5 ไฟล์ และ port ทั้งสามที่ต้องใช้ (`ISecretProtector`, `IStartupRegistrar`, `ISingleInstanceCoordinator`) มีอยู่แล้วใน `Application` จึงไม่ต้องแก้สัญญากลาง; เครื่องที่ใช้ทำงานยังไม่มี .NET SDK (`global.json` ปัก 10.0.401)
- 2026-10-26 — คุณรับรอง R-014 และอนุมัติ pipeline ทั้งเฟส 0–8 ให้ทำต่อเนื่องโดยไม่หยุดทบทวนหลังเฟส 4 โดยรับทราบความเสี่ยงที่ UI ในเฟส 5–7 อาจต้องแก้หรือพอร์ตซ้ำเมื่อ [[R-002]] ปิด
- 2026-10-26 — **ข้อยกเว้นจาก `Docs/AI_WORKFLOW.md` §5.1 ที่คุณอนุมัติ**: รอบนี้ให้ Claude Code (Coding Owner) เป็นผู้เขียนโค้ดเอง เพราะ Antigravity ซึ่งเป็น Writing Owner ไม่อยู่ใน session นี้ · Reviewer ยังเป็นสองตัวแยกกันอิสระตามเดิม (Codex และ Claude Code/Fable 5.1) · ข้อยกเว้นนี้ใช้กับ R-014 รอบนี้เท่านั้น ไม่เปลี่ยน roster ของโปรเจกต์
- 2026-10-26 — คุณอนุมัติให้ติดตั้ง .NET SDK 10.0.401 ด้วย `dotnet-install.sh` ลง `~/.dotnet` (ไม่ใช้ sudo ไม่แตะ `/usr/local` ถอนได้ด้วยการลบโฟลเดอร์)
- 2026-10-26 — พักงานตามคำสั่งคุณหลังเตรียม requirement เสร็จ ยังไม่เริ่มเฟส 0: **ยังไม่ได้ติดตั้ง .NET SDK** (รันเฉพาะ `--dry-run` เพื่อดู URL ปลายทาง) และยังไม่มีโค้ดใดถูกแก้ สถานะจึงคงเป็น ✅ ไม่ใช่ 🔨

- 2026-10-30 — **เฟส 0 เสร็จ** ติดตั้ง .NET SDK 10.0.401 ลง `~/.dotnet` (SHA-256 ของ `dotnet-install.sh` ตรงกับที่ตรวจไว้) แล้วเก็บ baseline จริงบน macOS: `dotnet restore AIMonitor.sln` ล้มเฉพาะสองโปรเจกต์ WPF ด้วย `NETSDK1100` · `Infrastructure` คอมไพล์ผ่านทั้งที่ยังปัก `net10.0-windows` · เทสต์แดง 7 ตัวคือ `CodexExecutableLocatorTests` 3 (fixture เขียน path แบบ `C:\...` แต่ `Path.Combine` ต่อด้วย `/` บน macOS — ตัวตรรกะไม่ผิด) · `WindowsStartupRegistrarTests` 3 (`Path.GetFullPath` ตีความ `C:\...` เป็น relative) · `UpdateDownloaderTests` 1
- 2026-10-30 — **เฟส 1 เสร็จ** ย้าย `AIMonitor.Infrastructure` และ `AIMonitor.Infrastructure.Tests` เป็น `net10.0`; CA1416 ฟ้องไฟล์เดียวคือ `LegacyRegistrySettingsReader` จึงใส่ `[SupportedOSPlatform("windows")]` ที่ ctor ไร้พารามิเตอร์ กับสมาชิกที่แตะ registry เท่านั้น ไม่ใส่ที่ type เพื่อให้ `ReadAll` ยังทดสอบข้ามแพลตฟอร์มได้; เพิ่ม `WindowsOnlyFactAttribute` แทน `if (!OperatingSystem.IsWindows()) return;` ที่รายงานว่า "ผ่าน"; แก้ fixture ของ Codex ให้ใช้ `Path.Combine` แทน literal จึงรันได้จริงทั้งสอง OS ไม่ต้อง skip; **เจอบั๊กโปรดักชันจริงหนึ่งตัว** — `UpdateDownloader` ใช้ `Path.GetFileName` ตัดชื่อไฟล์ของ asset ที่มาจาก GitHub ซึ่งตัดเฉพาะ separator ของ host เท่านั้น ชื่อ `..\..\evil\Setup.exe` จึงรอดทั้งก้อนบน macOS/Linux แก้ให้ตัดทุก separator ที่สองแพลตฟอร์มใช้ (ผลฝั่ง Windows ไม่เปลี่ยน) พร้อม `[Theory]` สามเคสที่แดง 2 เมื่อย้อนโค้ด; ยืนยันว่าทั้งสองโปรเจกต์ WPF ยัง compile ผ่านด้วย `-p:EnableWindowsTargeting=true` เพื่อกัน CA1416 regression ฝั่ง Windows
- 2026-10-30 — **เฟส 2 เสร็จ** เพิ่ม `AIMonitor.CrossPlatform.slnf` (solution ลบสองโปรเจกต์ WPF) · `.github/workflows/ci-macos.yml` บน `macos-15` เป็นไฟล์แยก **ไม่แก้ `ci.yml` เดิม** เพื่อไม่ให้ชื่อ required check ของ Windows เปลี่ยน · `scripts/assert_skips_are_documented.py` อ่าน `.trx` แล้วล้มงานถ้ามี skip ที่เหตุผลไม่ขึ้นต้นด้วย `Windows-only:`/`macOS-only:` (ทดลองกับ trx ปลอมแล้วได้ exit 1 ตามคาด) · เขียนส่วน macOS ใน `Docs/TESTING.md` §1 และ §7 กับ dependency rule ใน `Docs/ARCHITECTURE.md` §5
- 2026-10-30 — **เฟส 3 เสร็จ** เขียน macOS adapter สามตัวหลัง port เดิม โดยแยก "ส่วนประกอบคำสั่ง/ข้อความ" ที่เป็น static ล้วนออกจาก "ส่วนที่แตะ OS" ตามแบบที่ `CodexExecutableLocator`/`AgyUsageRunner` ใช้อยู่ จึงทดสอบได้ทุก OS ไม่ต้อง skip: `KeychainSecretProtector` (AES-GCM 256 ใต้ key ใน login Keychain, blob มี version byte + nonce + tag, อ่าน key ผ่าน stdout และเขียนผ่าน stdin ของ `security -i` เท่านั้น) · `LaunchAgentPlist` + `LaunchAgentStartupRegistrar` (เขียน plist, `Unregister()` ลบไฟล์เดียว ไม่เรียก launchctl เลย, `GetRegistrationState()` เช็ค `launchctl print-disabled` ด้วยเพราะ Login Items เขียน override ทับไม่ได้ลบไฟล์, คืน `null` เมื่ออ่านไฟล์ไม่ได้เพื่อรักษาเจตนาที่ผู้ใช้บันทึกไว้) · `LocalSocketSingleInstance` (Unix domain socket ตัวเดียวเป็นทั้ง lock และช่อง activation ใช้ wire format เดียวกับ named pipe ฝั่ง Windows, กันความยาว path 104 ไบต์ของ `sun_path`, ลบ socket ค้างจากตัวที่ crash); **เทสต์ที่เขียนใหม่จับบั๊กของตัวเองสองตัว** — plist ประกาศ `encoding="utf-16"` เพราะ `XmlWriter` เขียนลง `StringWriter` (Apple parser ปฏิเสธ) และ `Unregister()` โยน `DirectoryNotFoundException` เมื่อยังไม่มี `~/Library/LaunchAgents`; เพิ่มเทสต์ที่ให้ `plutil -lint` ของ macOS เองตรวจ plist จริง
- 2026-10-30 — **เฟส 4 เสร็จ** เพิ่ม `PlatformAdapters` เป็นที่เดียวที่เลือก adapter ตาม OS (รวม `ILegacySettingsReader`/`ILegacySecretUnsealer` ที่ฝั่ง macOS คืนค่าว่างแทนการโยน เพราะ `LegacyDpapiSecretUnsealer` จะ `PlatformNotSupportedException` บน macOS) พร้อมเทสต์ที่ยืนยันการเลือกของทั้งสอง OS จากเทสต์ชุดเดียว; เพิ่ม `tools/AIMonitor.MacOSSmoke` (อยู่นอก sln เหมือน `AIMonitor.ScreenEvidence`) ที่รัน adapter จริงกับ macOS จริงโดยใช้ Keychain service / LaunchAgent label / socket path แยกและเก็บกวาดให้เอง — **ผ่านทั้ง 17 ข้อบน macOS 27.0.1 arm64** รวมทั้งเปิดโปรเซสที่สองจริงแล้วตัวที่สองออกด้วย exit 0 ขณะที่ตัวแรกได้รับ argument; **ยังไม่ได้ต่อ `App.xaml.cs` ของ WPF เข้ากับ `PlatformAdapters`** เพราะเทสต์ของ WPF รันบน macOS ไม่ได้ จึงไม่แก้โค้ดที่ตรวจไม่ได้บนเครื่องนี้
- 2026-10-30 — **สองเรื่องที่ต้องให้คุณตัดสิน** (1) `LaunchAgentStartupRegistrar` ใช้ label `net.apichart.aiusagemonitor` ตัวเดียวกับ 1.x ตามหลัก in-place upgrade ของ ADR-0003 แต่เครื่องนี้**มี login item ของ 1.x อยู่จริง** (ชี้ไป `~/Documents/Apps/AIMonitor/dist/AI Usage Monitor.app`) การเปิด/ปิดสวิตช์ใน 2.0 จะทับหรือลบรายการนั้น ถ้าต้องการให้ 1.x กับ 2.0 อยู่แยกกันต้องเปลี่ยน label เป็นค่าอื่น (2) ยังไม่มีการ import settings/key ของ 1.x บน macOS (1.x เก็บใน `~/Library/Preferences` plist กับ Keychain ของตัวเอง) ผู้ใช้ macOS จึงเริ่มจากโปรไฟล์ว่าง ถ้าต้องการให้ย้ายข้อมูลมาต้องตั้งเป็นข้อกำหนดแยก
- 2026-10-31 — **เฟส 5 ลงโครงแล้ว ยังไม่ครบ** สร้าง `src/AIMonitor.Presentation.Avalonia` (Avalonia 12.1.4 · `net10.0` · MIT) เข้า `AIMonitor.sln` และ `AIMonitor.CrossPlatform.slnf` พร้อม composition root ที่เรียกผ่าน `PlatformAdapters` อย่างเดียว · **แอปเปิดจริงบน macOS 27.0.1 arm64 แล้ว** แสดง dashboard สามแท็บ (Claude · OpenAI / Codex · Gemini / AGY) ด้วยข้อมูลจริงจากชั้น Application, มี refresh, ตัวเลือกธีม, การ์ด error/setup hint, meter พร้อมแถบและป้ายระดับความรุนแรง (ไม่ใช้สีอย่างเดียว); เปิดซ้ำแล้วตัวที่สองออก exit 0 โดยตัวแรกยังอยู่ และปิดแล้วไม่ค้างโปรเซส; ยังขาดของเฟส 5 ที่เหลือคือ chart/history/supplementary details ให้เท่า WPF
- 2026-10-31 — เพิ่ม `AI_USAGE_MONITOR_SETTINGS_SCOPE` ตามที่ข้อนี้ระบุไว้ โดยต่อท้าย **ทั้งสี่อย่าง** (โฟลเดอร์ข้อมูล · Keychain service · LaunchAgent label · socket) ไม่ใช่แค่ settings เพราะสาขา `Mac` บันทึกไว้ว่า scope ที่ครอบไม่ครบยังลบ login item จริงและทับ key จริงได้ ทดลองรันจริงด้วย scope `devrun` แล้วพบว่าสร้าง `net.apichart.aiusagemonitor.devrun.plist` แยกจริง ส่วน plist ของ 1.x ไม่ถูกแตะ แล้วลบของ `devrun` ทิ้งหลังทดสอบ
- 2026-10-31 — **เรื่องที่สามที่ต้องให้คุณตัดสิน — ใหญ่กว่าสองข้อบน**: `ClaudeLiveQuotaClient` หา login จากไฟล์ `~/.claude/.credentials.json` ซึ่ง **ไม่มีบน macOS** (ยืนยันบนเครื่องนี้แล้วว่าไม่มีไฟล์) แอปที่เปิดได้จริงจึงรายงานว่า "No Claude Code login found" ตลอด แปลว่า provider หลักใช้งานไม่ได้บน macOS ตรงกับที่สาขา `Mac` จดไว้ว่าล็อกอินอยู่ใน Keychain ไม่ใช่ไฟล์ การแก้ต้องเพิ่มทางอ่าน credential จาก Keychain ให้ provider Claude ซึ่งอยู่นอกเฟส 0–8 ที่ตกลงกันไว้
- 2026-10-31 — **คำถามที่ค้างก่อนทำเฟส 5 ต่อ**: ViewModel ของ WPF มี ~3,300 บรรทัดและผูกกับ WPF จริงแค่ไม่กี่จุด (`ICommand` ใช้ร่วมกันได้ · ติด `Brush`/`Dispatcher`/`SaveFileDialog` แค่ 5 ไฟล์) จะเลือกทางไหนระหว่าง (ก) ดึง ViewModel ที่เป็นกลางออกเป็นโครงการ `AIMonitor.Presentation.Core` ให้ทั้งสองฝั่งใช้ร่วมกัน — โค้ดชุดเดียวไม่เพี้ยน แต่ต้องแก้โครงการ WPF ซึ่งเทสต์ของมันรันบน macOS ไม่ได้ และจะชนกับงาน [[R-002]] ที่ยังทำค้างอยู่ กับ (ข) ให้ฝั่ง Avalonia เขียน ViewModel ของตัวเอง — ไม่แตะ Windows เลย แต่ logic การแสดงผลจะมีสองชุดและเพี้ยนจากกันแน่นอน รอบนี้ผมเลือก (ข) แบบจำกัดวงไว้ก่อน (เขียนเฉพาะ ViewModel ของ dashboard ที่จำเป็น) เพื่อไม่ตัดสินใจแทนคุณในเรื่องที่ย้อนยาก
- 2026-10-31 — **คุณตัดสินสามข้อแล้ว**: (1) Claude login บน macOS — *"แก้เลยในรอบนี้"* (2) ViewModel — *"ให้ Avalonia เขียนของตัวเอง"* (3) LaunchAgent label — *"ใช้ label เดิม"* บันทึกไว้เป็นคำตัดสิน ไม่ต้องถามซ้ำ
- 2026-10-31 — **แก้เรื่อง Claude login แล้ว** เพิ่ม `ClaudeKeychainCredentialReader` (service `Claude Code-credentials` · อ่านอย่างเดียว ไม่เขียนทับ เพราะ Claude Code เป็นเจ้าของรอบ refresh ของ token · deadline 2 นาทีเพราะการอ่านครั้งแรกเด้งกล่องขออนุญาตที่ผู้ใช้ต้องกดเอง ACL ของ item ชี้ไป Claude Code ไม่ใช่แอปเรา) กับ `ChainedClaudeCredentialReader` ที่ไล่ไฟล์ก่อนแล้วค่อย Keychain และ**ไปต่อเฉพาะเมื่อผลเป็น `Missing`** เท่านั้น — login ที่มีอยู่แต่เสียหรือหมดอายุต้องรายงานตามจริง ไม่ใช่ถูกกลบด้วยแหล่งถัดไป (ตรงกับ `load_raw()` ของสาขา `Mac`); `PlatformAdapters.CreateClaudeCredentialReader()` เป็นตัวเลือกให้ตาม OS; **ยืนยันบนเครื่องจริงด้วย smoke tool แล้วว่าเครื่องนี้ไม่มีไฟล์ `.credentials.json` แต่มี Keychain item อยู่** (ถามเฉพาะ metadata ไม่อ่านค่า จึงไม่มีกล่องขออนุญาตเด้ง)
- 2026-10-31 — **ที่ยังไม่ได้ยืนยันด้วยตา**: การอ่านค่า token จริงจาก Keychain (`-w`) จะเด้งกล่อง "allow access?" ครั้งแรกซึ่งต้องให้คุณกดเอง และตอนทดสอบรอบหลังจอเครื่องหลับไปแล้ว (`Display Asleep: Yes`) ทำให้ Avalonia เปิดไม่ขึ้น (`RenderTimer` error -6661) และถ่ายภาพหน้าจอไม่ได้ — ไม่ใช่ปัญหาของโค้ด ให้ลองเปิดแอปอีกครั้งตอนจอตื่นเพื่อดูว่าแท็บ Claude ขึ้นโควตาจริง
- 2026-10-31 — สถานะเครื่องหลังทำงาน: ไม่มีโปรเซสค้าง · ไม่มีโปรไฟล์ scope ทดลองเหลือ · `~/Library/LaunchAgents` มีแค่ plist ของ 1.x ตามเดิม · ไม่เคยสร้าง Keychain item ของ 2.0 จริง (`AIMonitor2.secrets.v1`) เพราะยังไม่มีใครบันทึก API key
- 2026-10-31 — **เฟส 6 เสร็จ** เมนูบาร์ macOS แทน tray ของ Windows: `MenuBarHost` ใช้ `TrayIcon` ของ Avalonia (บน macOS คือ NSStatusItem) · `MenuBarIconRenderer` วาดไทล์เองด้วย Avalonia แบบเดียวกับไทล์ฝั่ง Windows (เติมจากล่างตามเปอร์เซ็นต์ ตัวเลขวาดสองครั้งเพราะเส้นแบ่งมักพาดตัวเลข) และ**ไม่ใช้ template icon** เพราะ macOS จะบีบให้เหลือสีเดียว ซึ่งจะทิ้งระดับความรุนแรงที่ไทล์มีไว้เพื่อบอก · เมนูเปิดด้วยคลิกเดียวตามธรรมเนียม macOS จึงไม่มีท่า double-click ซ่อนไว้ แต่ใส่ "Open AI Usage Monitor" เป็นรายการแรกแทน · หมุนเปลี่ยนบริการทุก 2 วินาทีเฉพาะเมื่อมีข้อมูลมากกว่าหนึ่งบริการ · widget เป็นหน้าต่างไร้กรอบ ลากย้ายได้ ปักหมุดได้ โปร่งแสงและลอยบนสุดตามค่าที่ตั้งไว้ · ตั้งโครงการเทสต์ `tests/AIMonitor.Presentation.Avalonia.Tests` (รันได้ทุก OS เพราะตรรกะที่ทดสอบไม่แตะ toolkit)
- 2026-10-31 — **เฟส 7 เสร็จ** กล่องโต้ตอบสี่ตัว Settings · Connections · About · Usage log พร้อมเมนูแอปบนแถบบนสุดของ macOS (`NativeMenu`) และปุ่มในหัว dashboard · **Cancel ของ Settings คืนค่าทุกช่องจริงโดยโครงสร้าง** ไม่ใช่โดยการไล่ undo ทีละช่อง เพราะหน้านี้แก้สำเนาและเขียนจริงตอน Save เท่านั้น (มีเทสต์ยืนยันว่าแก้แล้วกด Cancel ไม่มีอะไรถูกเขียนเลย) · ช่อง API key **ไม่เคยเติมค่าที่เก็บไว้กลับมาแสดง** และการล้าง key เป็นปุ่มแยกไม่ใช่ "ปล่อยช่องว่าง" เพื่อไม่ให้ใครลบ key โดยไม่ตั้งใจ · การล้างเรียกถึง Keychain จริง ไม่ใช่แค่ลบค่าใน settings ตามที่สาขา `Mac` เตือนไว้
- 2026-10-31 — **เฟส 8 เสร็จ (ส่วนที่ build ได้)** `scripts/build_macos_app.sh` + `build/macos/Info.plist.template` สร้าง `AI Usage Monitor.app` แบบ **one-directory + self-contained ไม่ใช่ single-file** · เซ็น ad-hoc เท่านั้น (ขั้นต่ำที่ binary arm64 ต้องมี) ไม่เซ็น Developer ID ไม่ notarize ตามที่ข้อนี้กันไว้ · `LICENSE` กับ `THIRD-PARTY-NOTICES.md` อยู่ทั้งใน `Contents/MacOS/` และ `Contents/Resources/` · เพิ่ม Avalonia / SkiaSharp / HarfBuzzSharp / MicroCom (MIT ทั้งหมด) ลง `THIRD-PARTY-NOTICES.md` และแก้ย่อหน้า .NET ให้บอกว่า macOS ไม่ได้ใช้ WPF/WinForms/GDI+ · **ผลจริง**: `plutil -lint` ผ่าน · `codesign --verify --deep --strict` ผ่าน · `CFBundleShortVersionString` = 2.0.2 อ่านจาก `VersionPrefix` ที่เดียว · Mach-O arm64 · 111 MB
- 2026-10-31 — **สถานะชุดทดสอบหลังเฟส 8**: 1,356 ผ่าน / 21 skip / 0 แดง (รวม 1,377) · `dotnet format` สะอาด · ไม่มี package ที่มีช่องโหว่ใน 9 โปรเจกต์ · โปรเจกต์ WPF ทั้งสองยัง compile ผ่าน
- 2026-10-31 — **ค้างไว้รอคุณตอนเช้า (ตามที่อีก session แจ้งว่าคุณเข้านอนและให้ทำเฉพาะงานที่ไม่เขียนไฟล์นอกโฟลเดอร์โปรเจกต์)**: (1) เปิดแอปดูด้วยตาว่าแท็บ Claude อ่านโควตาจาก Keychain ได้จริง — การอ่าน token ครั้งแรกจะเด้งกล่อง "allow access?" ที่ต้องกดเอง (2) เปิด `.app` จาก Finder (3) ดู widget, เมนูบาร์ และกล่องโต้ตอบทั้งสี่บนจอจริง ทั้งสามข้อเขียนไฟล์นอกโฟลเดอร์โปรเจกต์ (`~/Library/...`) จึงหยุดไว้ · ยังไม่ commit อะไรเลย
- 2026-10-31 — **คุณเปิดดูแล้วและสั่งแก้สามจุด ทำครบแล้ว**: (1) เมนูบนสุดของ macOS ขึ้นว่า "Avalonia Application" — ตั้ง `Name` ของ Application เป็น "AI Usage Monitor" (ยืนยันผ่าน CGWindowList แล้วว่า owner เปลี่ยนตาม) (2) ข้อความ AGY บอกว่า "Add agy.exe to PATH" ซึ่ง**ไม่ใช่แค่คำผิด**: `AgyUsageRunner` ค้นหาไฟล์ชื่อ `agy.exe` บน PATH จริง ๆ บน macOS จึงหาไม่เจอตลอดแล้วไปบอกผู้ใช้ให้ติดตั้งสิ่งที่เขามีอยู่แล้ว — เพิ่ม `ExecutableNames.OnThisPlatform()` แล้วแก้ทั้ง AGY และ `CodexExecutableLocator` ซึ่งเป็นบั๊กเดียวกัน (3) หน้า dashboard แสดงเป็นแถบเส้น ทั้งที่ต้นฉบับบน Windows เป็น gauge — เขียน `Controls/GaugeControl.cs` วาดเองด้วย `DrawingContext` ของ Avalonia ใช้เรขาคณิตชุดเดียวกับฝั่ง Windows เป๊ะ (กวาด 260° เริ่มที่ 140° · ปลายโค้งมน · รัศมี 0.38 ของด้านสั้น · ความหนา 0.16 ของรัศมี) วางเป็นการ์ดเรียงแนวนอนพร้อม "✓ Normal" กับเวลารีเซ็ตใต้วง แบบเดียวกับ `MainWindow.xaml` ของ WPF และใช้วงเดียวกันนี้ใน widget ด้วย
- 2026-10-31 — **เห็นกับตาแล้วว่าใช้ได้จริง** เปิด `.app` ผ่าน LaunchServices (`open`) ไม่ติด Gatekeeper เพราะ build เองในเครื่อง · แท็บ Claude อ่านโควตาจาก Keychain ได้จริง (Session 58% · Weekly 16% / 15%) · **ไม่มีกล่องขออนุญาต Keychain เด้งเลย** เพราะอ่านผ่าน `/usr/bin/security` ตัวเดียวกับที่ Claude Code ใช้สร้าง item ACL จึงตรงกันอยู่แล้ว · หน้า Connections ขึ้นว่า Claude "Connected" ส่วน OpenAI "Not signed in" และ AGY บอกว่าหา CLI ไม่เจอ ตามจริงทั้งคู่
- 2026-10-31 — ชุดทดสอบหลังแก้: 1,371 ผ่าน / 21 skip / 0 แดง (รวม 1,392) · `dotnet format` สะอาด · WPF ทั้งสองโปรเจกต์ยัง compile ผ่าน · ยังไม่ commit
- 2026-10-31 — **คุณสั่งให้ไปไล่เทียบกับฝั่ง Windows ให้ครบ ทำแล้ว** เจอความต่างเจ็ดจุดและ**บั๊กจริงหนึ่งตัว**: มิเตอร์ใช้ `ServerSeverity` ตรง ๆ ทั้งที่ Windows ใช้ `EffectiveSeverity` (เอาค่าที่แย่กว่าระหว่างเกณฑ์ภายในกับที่บริการรายงาน) ผลคือมิเตอร์ที่ 95% จะขึ้นว่า Normal ถ้าบริการไม่ได้บอกว่าผิดปกติ · จุดอื่นที่แก้: แท็บเป็นเส้นใต้ขนาดปกติและพกค่านำหน้า ("Claude 83%") · การ์ดตั้งค่าโผล่เฉพาะเมื่อ `!Configured && ไม่มีมิเตอร์` พร้อมปุ่ม "Configure …" · แบ่งเกจเป็น section มีหัวข้อและทุกแถวใช้จำนวนคอลัมน์เดียวกัน · แถว stat tiles · แบนเนอร์ error มี ⚠ · หมายเหตุ history แยกจาก error เพื่อไม่ให้ดูเหมือนโควตาสดล้มไปด้วย · ชื่อการ์ดตัดคำนำหน้า "Section · " ที่หัวข้อมีอยู่แล้ว
- 2026-10-31 — **เฟสกราฟเสร็จ (คุณสั่งเพิ่มพร้อมรายงาน token)** พอร์ต `StackedColumnChartControl` และ `HorizontalBarChartControl` มาเป็นของ Avalonia วาดเองด้วย `DrawingContext` ตามกฎเดียวกับ Windows ทุกข้อ (แท่งหนาไม่เกิน 24px ปลายมน 4px · เส้นกริดบนค่าที่ลงตัว 4 ช่วง · ช่องว่าง 2px ระหว่างท่อนที่ติดกัน · ป้ายค่าบนยอดแท่งเฉพาะที่ไม่ชนกัน · ป้ายวันที่บางลงจนไม่ชนกัน · legend เมื่อมีตั้งแต่สอง series · แถวละ 30px สูงสุด 8 แถวแล้วพับที่เหลือเป็น "Other") พร้อม `ChartFormat` และจานสี 8 สีชุดเดียวกับ Windows เพื่อให้โมเดลเดียวกันได้สีเดียวกันทั้งสองแพลตฟอร์ม · เปิด `includeHistory: true` ในการ refresh ซึ่งเดิมปิดอยู่จึงไม่เคยมีข้อมูลมาเลย
- 2026-10-31 — **ผลจริงบนเครื่อง**: การแก้ชื่อไฟล์ `agy.exe`/`codex.exe` ทำให้ **ทั้งสาม provider ใช้งานได้พร้อมกันครั้งแรกบน macOS** — Claude 83% (Session ขึ้น ⚠ High จาก `EffectiveSeverity` ที่เพิ่งแก้) · OpenAI / Codex 18% พร้อม Plan Plus · Gemini / AGY 14% แสดง section "Gemini" กับ "Claude / GPT" เรียงคอลัมน์ตรงกัน · กราฟรายวัน 14 วันแยกสีตามโมเดล (Opus 5 / Opus) แกนลงตัวที่ 50M/100M/150M/200M · stat tile "Equivalent API value $128.83" · AGY ไม่มี history จึงไม่ขึ้นกราฟ ตามที่บริการรายงานเอง
- 2026-10-31 — ชุดทดสอบ: 1,433 ผ่าน / 21 skip / 0 แดง (รวม 1,454) · `dotnet format` สะอาด · `.app` build ใหม่แล้ว · ยังไม่ commit
- 2026-10-31 — **คุณสั่งแก้ gauge ของ Gemini 4 Argon ที่ชื่อกินพื้นที่วง** ต้นเหตุคือ `GaugeControl` วาดชื่อโดยไม่จำกัดความกว้าง ชื่อยาว "Gemini 4 Argon · Weekly" จึงพาดข้ามปลายโค้ง · แก้ที่ตัววาด (ไม่แตะ `AgyUsageMapper` ที่ใช้ร่วมกับ Windows): ชื่อต้องอยู่ในความกว้างระหว่างขอบในของวงที่ระดับความสูงของชื่อ — ย่อฟอนต์ได้ไม่ต่ำกว่า 80% ถ้ายังไม่พอให้ขึ้นบรรทัดที่สอง และตัด "…" เฉพาะส่วนที่สองบรรทัดยังไม่พอ ชื่อที่ขึ้นสองบรรทัดจะดัน subtitle ลงแทนการทับกัน · เทสต์ใหม่ 5 ข้อใน `Controls/GaugeControlTests.cs` · **ยังไม่ได้ยืนยันด้วยตาบนจอจริง** เพราะถ่ายภาพหน้าต่างไม่สำเร็จ

## R-013 · แบ่ง gauge ของ AGY เป็นกลุ่ม Gemini และ Claude/GPT และเปลี่ยนชื่อเป็น Gemini / AGY

| | |
|---|---|
| สถานะ | ✔️ เสร็จแล้ว (ผู้ใช้ตรวจบนแอปจริง 2026-10-16; ออกใน 2.0.2) |
| วันที่ | 2026-10-16 |
| แตะส่วนไหน | `Meter.Section` · `AgyUsageMapper` · `ProviderTabViewModel.MeterSections` · gauge row ใน `MainWindow.xaml` · display name |
| เกี่ยวกับ | [[R-012]] · [[R-010]] |

**คุณระบุ:**
> ผมมีปัญหาเรื่อง Design คือ Guage นั้น เมื่อมีหลายๆ มิเตอร์ ก็จะทำให้การจัดหน้าไม่พอดี โดยเฉพาะในหน้าของ AGY ซึ่ง "Gemini - Session" จะยาวจนทับ guage อาจจะต้องแบ่งกลุ่มไหมครับ เป็นกลุ่ม Gemini กลุ่มหนึ่ง และ Claude/GPT กลุ่มหนึ่ง ผมเปิดหน้าจอไว้ให้แล้วครับ ลองตรวจดู และชื่อจาก AGY ให้เปลี่ยนเป็น Gemini/AGY

**ผมเข้าใจว่า:**
หน้า AGY แสดง gauge เป็นแถวละกลุ่มพร้อมหัวข้อ "Gemini" (รวม pool เฉพาะรุ่นเช่น Gemini 4 Argon) และ "Claude / GPT" ทุกแถวใช้จำนวนคอลัมน์เท่ากันเพื่อให้การ์ดตรงกัน ชื่อบนการ์ดตัดส่วนที่ซ้ำกับหัวข้อออก ("Session", "Weekly") ส่วน widget/tray ใช้ชื่อเต็มเหมือนเดิม ชื่อ provider ที่แสดงเปลี่ยนจาก "AGY" เป็น "Gemini / AGY" (เว้นวรรครอบ / ให้ตรงกับ "OpenAI / Codex")

**ไม่รวม:**
- ไม่แก้ `GaugeControl` และไม่เปลี่ยนหน้าตา Claude/OpenAI (ไม่มี section จึงเป็นแถวเดียวเหมือนเดิม)
- ไม่เปลี่ยน provider id `agy`, ข้อความ "AGY CLI" หรือ settings

**ตรวจรับเมื่อ:**
- [x] หน้า Gemini / AGY แบ่งเป็นกลุ่ม Gemini และ Claude / GPT ชื่อไม่ทับ gauge
- [x] tab, Connections, Connect dialog และ Usage log ใช้ชื่อ Gemini / AGY
- [x] regression tests ของ mapper และ ViewModel; full suite 1,489 ผ่าน, build 0 warnings/errors, format ผ่าน
- [x] ผู้ใช้ตรวจหน้าตาบนแอปจริง

## R-012 · แทน Gemini Cloud Monitoring ด้วย AGY อย่างเดียว

| | |
|---|---|
| สถานะ | ✔️ เสร็จแล้ว (ผู้ใช้ยืนยันข้อมูลจริงบน Debug build 2026-10-16) |
| วันที่ | 2026-10-15 |
| แตะส่วนไหน | AGY CLI provider · composition · tab/connections/tray/reports · settings compatibility · tests/docs |
| เกี่ยวกับ | R-001/R-006 เฉพาะ Gemini slice · Docs/AGY-Usage.md |

**คุณระบุ:**
> ตัด gemini ทิ้งไปเลยครับ ให้ดึงข้อมูลผ่าน AGY อย่างเดียว

**ผมเข้าใจว่า:**
เปลี่ยน provider ที่ใช้งานจริงจาก Gemini Cloud Monitoring เป็น AGY (id agy) อ่านโควตาผ่าน `agy -p "/usage" --output-format json` เท่านั้น แสดงกลุ่ม Gemini Models และ Claude/GPT ที่ AGY รายงานเป็น shared pools โดยคง gauge เดิมและไม่ปนกับ standalone Claude/Codex ใช้ remaining_fraction แปลงเป็น used % และ reset_time เป็นเวลานัด reset หน้า Connections ไม่มี key/service-account/project สำหรับ AGY ย้าย active provider และ Enabled เดิมอย่างรักษาข้อมูล และแสดงข้อผิดพลาดที่แก้ต่อได้เมื่อ CLI/ข้อมูลไม่พร้อม

**ขอบเขตที่แทนข้อเดิม:**
คำสั่งล่าสุดแทน baseline Gemini Cloud Monitoring/PAR-011/PAR-012 และส่วน Gemini ของ R-006 เฉพาะเส้นทางที่ใช้งานจริง ไม่เปลี่ยนประวัติการตรวจรับของข้อเดิม ส่วน Claude/OpenAI เดิมคงอยู่

**ไม่รวม:**
- ไม่ลบ credentials/settings เก่า ไม่อ่านไฟล์ auth จริงโดยตรง ไม่เปลี่ยน AGY login
- ไม่ fallback ไป Google API/Gemini CLI/ADC/service account และไม่สร้าง token/spend/history จาก quota
- ไม่ลบ legacy Gemini source/tests ที่มีงาน uncommitted; เก็บไว้เฉพาะ compatibility/history โดยไม่ register ใช้งาน
- ไม่เปลี่ยน GaugeControl ไม่ใช้สิทธิ์ reset ไม่ deploy/publish/installer

**ตรวจรับเมื่อ:**
- [x] runtime/UI ใช้ AGY แทน Gemini Cloud Monitoring และไม่มี fallback เก่า
- [x] แสดงทุกกลุ่มและรอบ 5h/weekly พร้อม used %, unknown/reset semantics ถูกต้อง
- [x] process มี deadline/cancellation/output bounds/cleanup และไม่แสดง raw error/credentials
- [x] settings migration รักษา Enabled/active provider โดยไม่ย้าย key/project ไป AGY
- [x] synthetic tests, independent Claude/Codex reviews, build/format/render ผ่าน
- [x] เปิด build ใหม่ให้ผู้ใช้ตรวจรับข้อมูลจริง

**หลักฐาน:** 2026-10-16 — tests 1,486 ผ่าน, build 0 warnings/errors, format ผ่าน, synthetic WPF renders 40 ภาพ; ดู `Docs/HANDOFF_R012.md`; 2026-10-16 ผู้ใช้ตรวจข้อมูลจริงบน Debug build แล้วผ่าน

## R-011 · ผูก section ข้อมูลเพิ่มเติมเข้า WPF จริง

| | |
|---|---|
| สถานะ | 🧪 รอตรวจรับ |
| วันที่ | 2026-10-15 |
| แตะส่วนไหน | MainWindow · component ข้อมูลเพิ่มเติม · ProviderTabViewModel · CodexQuotaMapper · synthetic tests |
| เกี่ยวกับ | R-007 · R-009 · R-010 |

**คุณระบุ:**
> ผูกเข้าแอพเลยครับ

**ผมเข้าใจว่า:**
อนุมัติผูกหน้าข้อมูลเพิ่มเติมตามแบบร่างเข้าแอป WPF จริง โดยเพิ่ม component แยกใต้ gauge คงโค้ดวาดเกจและ quota logic เดิม ใช้ข้อมูลจริงจาก snapshot/คำขอ Codex เดิมสำหรับ plan, workspace credits, earned reset count/known expiry และ activity ที่ service ส่งมา พร้อมรายละเอียดที่ขยายได้ ข้อมูลหายต้องแสดงไม่ทราบหรือไม่แสดง ห้ามใช้ข้อมูลจำลองในแอปจริง และ Claude reset แสดงยังตรวจไม่ได้พร้อมคำแนะนำตรวจผ่าน web/Desktop

**ไม่รวม:**
- ไม่ redeem/reset จริง ไม่มอบ credits หรือเปลี่ยน login/account
- ไม่เพิ่มคำขอเครือข่ายใหม่ ไม่แก้ widget หรือ GaugeControl
- AGY/Gemini integration ตาม Docs/AGY-Usage.md เป็นงานแยกถัดไป

**ตรวจรับเมื่อ:**
- [x] component ใหม่อยู่ใต้ gauge เดิม รองรับ theme และหน้าจอแคบ
- [x] ข้อมูล Codex present/zero/null/malformed และ count ต่างจากรายการ details แสดงถูกต้อง ไม่สร้างค่าขึ้นเอง
- [x] Claude แสดงยังตรวจไม่ได้พร้อมคำแนะนำ ไม่มีปุ่มใช้สิทธิ์จริง
- [x] refresh ไม่เหลือข้อมูลเก่า ไม่ซ้ำกับ legacy stats และไม่เปลี่ยน meters
- [x] independent reviews และ synthetic tests/build/format ผ่าน (1,457 passed, 0 warnings/errors, render 40 ภาพ; Docs/HANDOFF_R011.md)
- [x] เปิดแอป Release build ใหม่ให้ผู้ใช้ตรวจรับ (PID 19736)
- [ ] ผู้ใช้ตรวจรับหน้าจอและข้อมูลบัญชีจริง

**ความคืบหน้า:**
- 2026-10-15 — เพิ่ม component ข้อมูลเพิ่มเติมแยกใต้ gauge และผูกข้อมูล snapshot จริง; ผ่าน Claude/Codex independent review และแก้ fallback, setup/API mode, expansion persistence, narrow layout และ numeric boundaries แล้ว; integrator รัน build 0 warnings/errors, format ผ่าน, full tests 1,457 passed และ render 40 ภาพ พร้อมเปิด Release build ให้ผู้ใช้ตรวจรับ ดู Docs/HANDOFF_R011.md สำหรับคำสั่งและข้อจำกัดจริง

## R-010 · ปรับแบบร่างให้คง gauge และแยก section ข้อมูลเพิ่มเติม

| | |
|---|---|
| สถานะ | 🧪 รอตรวจรับแบบร่าง |
| วันที่ | 2026-10-15 |
| แตะส่วนไหน | artifacts/visuals/aimonitor-reset-preview.html · เอกสารส่งต่อ |
| เกี่ยวกับ | R-009 |

**คุณระบุ:**
> ปรับตามนั้นครับ และถ้าทดสอบผ่านแล้ว ลองเปิดไว้ให้ผมตรวจสอบ
> สร้าง section สำหรับแสดงผลใหม่ใต้ guage ครับ เพื่อให้มั่นใจว่าจะไม่กระทบโค้ดส่วนเดิม

**ผมเข้าใจว่า:**
ปรับแบบร่างให้ gauge วงโค้งเป็นส่วนหลักตามรูปแบบเดิม และแยก section ใหม่ใต้ gauge สำหรับ plan, credits, reset และข้อมูลเสริมเป็นแถวสั้น ๆ พร้อมรายละเอียดที่เปิดเพิ่มได้ ทดสอบด้วยข้อมูลจำลองและเปิดไว้ให้ผู้ใช้ตรวจ

**ไม่รวม:**
- ไม่แก้ GaugeControl, quota logic, WPF หรือ installer ในรอบแบบร่างนี้
- ไม่ใช้ข้อมูลบัญชีจริงหรือ redeem reset

**ตรวจรับเมื่อ:**
- [x] ทั้งสองแบบใช้ gauge วงโค้งและแยก section ข้อมูลเพิ่มเติมอยู่ด้านล่าง
- [x] สลับ provider/reset state และเปิดรายละเอียดได้ โดยไม่มี overflow ที่ 320/736/1024px (`python artifacts/r009_browser_check.py`)
- [x] เปิดแบบร่างให้ผู้ใช้ตรวจ และยืนยันว่าไม่มีการแก้โค้ด gauge/WPF ในงานนี้
- [ ] ผู้ใช้ตรวจรับรูปแบบ

## R-009 · แบบร่างข้อมูลเพิ่มเติมและสิทธิ์ reset ของ Codex/Claude

| | |
|---|---|
| สถานะ | 🧪 รอตรวจรับแบบร่าง |
| วันที่ | 2026-10-15 |
| แตะส่วนไหน | แบบร่างในบทสนทนา · Docs/HANDOFF_R009.md |
| เกี่ยวกับ | R-007 · R-008 |

**คุณระบุ:**
> ได้ครับ ใช้ข้อมูล ลองวาดส่วนแสดงผลข้อมูลอื่นๆ ด้วยครับ และเพิ่มสิทธิ์ reset quota ใน claude ให้ผมด้วยครับ
> reset quota ทั้งใน codex และ claude ครับ

**ผมเข้าใจว่า:**
- ทำแบบร่างหน้าข้อมูลเพิ่มเติมด้วยข้อมูลจำลองที่ระบุชัด พร้อมส่วนแสดงสิทธิ์ reset ทั้ง Codex และ Claude ตามคำชี้แจงล่าสุด
- Codex แสดงจำนวนสิทธิ์และวันหมดอายุเมื่อมีข้อมูล รวม activity streak และ longest running turn ตาม contract ทางการ
- Claude มี limit reset สำหรับบัญชีที่ได้รับข้อเสนอ; แสดงทางไป Settings > Usage และแยกสถานะยังตรวจไม่ได้ออกจากไม่มีสิทธิ์ ไม่อนุมานจำนวนสิทธิ์จาก quota meters
- แบบร่างช่วยตรวจการจัดวางและสถานะก่อนผูกข้อมูลจริง; แอปไม่สามารถมอบสิทธิ์ให้บัญชีแทนผู้ให้บริการ

**ไม่รวม:**
- ไม่ใช้หรือ redeem สิทธิ์ reset ของบัญชีจริง ไม่เพิ่ม credits ไม่แก้แพ็กเกจ
- ไม่อ่าน credentials หรือเรียก live account; ไม่สร้าง Claude private API จากการคาดเดา
- รอบแบบร่างนี้ยังไม่เปลี่ยนโปรแกรม WPF หรือ installer

**ตรวจรับเมื่อ:**
- [x] ดูแบบร่างได้ทั้ง Codex และ Claude พร้อมป้ายข้อมูลจำลอง
- [x] สลับสถานะมีสิทธิ์ / ไม่มีสิทธิ์ / ยังตรวจไม่ได้ โดยไม่เท่ากับศูนย์เมื่อข้อมูลหาย
- [x] ระบุทางใช้สิทธิ์ Claude ที่รองรับจริงและข้อมูล Codex ที่ service คืนได้
- [x] ตรวจ markup/script และ Git diff; ไม่มี live reset หรือ credentials
- [ ] ผู้ใช้ตรวจรับรูปแบบก่อนผูกข้อมูลเข้าโปรแกรมจริง

**ความคืบหน้า:**
- 2026-10-15 — Agy สร้างแบบร่าง Cards/Compact rows; ผ่าน independent Claude/Codex review และ Codex integration แก้ stale badge; browser synthetic checks ผ่าน 6 provider/state combinations และ layout 320/736/1024; เอกสาร `Docs/HANDOFF_R009.md`, `Docs/REVIEW_R009.md`. ยังไม่เปลี่ยน WPF หรือใช้สิทธิ์ reset จริง; task-specific CLI sessions ปิดแล้ว
