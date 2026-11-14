# Handoff — R-002 เฟส 2: settings มีผลทันทีกับ widget และ tray

จาก Coding Owner (Claude Code) ถึง Writing Owner (Antigravity) · branch `agent/antigravity-widget-tray-live-settings`
ฐาน: `feat/csharp-rewrite` @ `8f5a34e` (เฟส 1 merge แล้ว: `SettingsSession` เป็นแหล่งเดียวของ settings)

## Objective

ค่าตั้งที่ผู้ใช้เปลี่ยนใน Settings ต้องมีผล **ทันทีโดยไม่ต้องรีสตาร์ต**: opacity และ always-on-top ของ widget, และการแสดง/ซ่อน tray icon
พร้อมแก้บั๊กที่พบจากโค้ด: ถ้า `MinimizeToTray = true` แต่ `ShowTrayIcon = false` การปิดหน้าต่างจะซ่อนแอปโดยไม่มีไอคอนให้เรียกกลับ

### ข้อเท็จจริงที่พิสูจน์แล้วจากโค้ด

- `WidgetViewModel` ตั้ง `Opacity = 0.92` และ `AlwaysOnTop = true` ตายตัว ไม่มีโค้ดอ่าน `WidgetOpacity`/`WidgetAlwaysOnTop` จาก settings (slider ใน Settings จึงไม่มีผล)
- `App` สร้าง `TrayIconHost` ครั้งเดียวตอนเริ่มถ้า `ShowTrayIcon` เป็นจริง และส่งเข้า `MainWindowViewModel` ผ่าน constructor (readonly) จึงเปิด/ปิดระหว่างรันไม่ได้
- `MainWindow.OnClosing` ตัดสินซ่อนหน้าต่างจาก `MinimizeToTray` อย่างเดียว ไม่ดู `ShowTrayIcon`
- `App.OnStartup` ซ่อนหน้าต่างเมื่อมี `--minimized/--tray/-m` โดยไม่ดูว่ามี tray หรือไม่

## Scope (อนุญาตให้แก้)

- ใหม่: `src/AIMonitor.Application/Settings/TrayPolicy.cs` (ฟังก์ชันล้วน ไม่พึ่ง WPF)
- ใหม่: อินเทอร์เฟซเล็ก ๆ สำหรับรับค่า tray เช่น `ITrayReadingsSink` (ใน Presentation.Wpf/Tray) ที่ `TrayIconHost` implement
- แก้: `ViewModels/WidgetViewModel.cs`, `ViewModels/MainWindowViewModel.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, `Tray/TrayIconHost.cs` (เฉพาะการ implement อินเทอร์เฟซ)
- tests: `tests/AIMonitor.Application.Tests/Settings/`, `tests/AIMonitor.Presentation.Wpf.Tests/`

## ข้อกำหนด

1. `TrayPolicy`: `ShouldShowTray(AppSettings)`, `ShouldHideOnClose(AppSettings)` (จริงเฉพาะเมื่อ `MinimizeToTray` และ `ShowTrayIcon` เป็นจริงทั้งคู่), `ShouldStartHidden(AppSettings, bool startMinimizedRequested)` (ซ่อนได้เฉพาะเมื่อมี tray ให้เรียกกลับ)
2. `WidgetViewModel.ApplySettings(AppSettings)`: ตั้ง `Opacity` และ `AlwaysOnTop` จาก `WidgetOpacity`/`WidgetAlwaysOnTop` แล้วยิง property-changed เฉพาะเมื่อค่าเปลี่ยน `App` เรียกครั้งแรกตอนสร้าง VM ด้วย `session.Current` และเรียกซ้ำจาก `OnSettingsChanged` (ซึ่ง marshal เข้า UI thread ด้วย `InvokeAsync` อยู่แล้ว ห้ามเปลี่ยนเป็น `Invoke`)
3. `MainWindowViewModel`: เก็บ tray เป็น `ITrayReadingsSink?` ที่เปลี่ยนได้ผ่าน `AttachTray(ITrayReadingsSink?)`; เมื่อ attach ต้องส่งค่าอ่านล่าสุดให้ทันที (ถ้ามี) เมื่อ detach (null) ต้องไม่เรียก sink เดิมอีก
4. `App.OnSettingsChanged`: เมื่อ `ShowTrayIcon` เปลี่ยนจริง — true → สร้าง `TrayIconHost` ต่อ event ทุกตัวเหมือนตอนเริ่ม แล้ว `AttachTray`; false → `AttachTray(null)` แล้ว `Dispose` tray ห้ามสร้างซ้ำถ้ามีอยู่แล้ว ห้ามรั่ว (`OnExit` ยัง Dispose tray ตัวปัจจุบัน)
5. `MainWindow.OnClosing` ใช้ `TrayPolicy.ShouldHideOnClose(_session.Current)`; `App.OnStartup` ใช้ `TrayPolicy.ShouldStartHidden`
6. Refresh/tray rotation เดิมต้องทำงานเหมือนเดิม (PAR-022)

## Non-goals

- ไม่ทำ live preview ใน Settings (เฟส 4) ไม่แตะ XAML ไม่เพิ่มปุ่ม/ตัวเลือกใหม่
- ไม่เปลี่ยน schema `AppSettings`, ไม่แตะ `secrets.dat`, ไม่ผูก `WidgetRotationEnabled`
- ไม่เพิ่ม NuGet dependency, ไม่ refactor ส่วนอื่น

## Acceptance criteria

1. `ApplySettings` ตั้ง opacity/always-on-top ตาม settings (รวมค่าขอบ 0.25 และ 1.0) และไม่ยิง property-changed เมื่อค่าไม่เปลี่ยน
2. `TrayPolicy.ShouldHideOnClose` เป็นเท็จเมื่อ `ShowTrayIcon = false` แม้ `MinimizeToTray = true`; `ShouldStartHidden` เป็นเท็จเมื่อไม่มี tray
3. `AttachTray` ส่งค่าอ่านล่าสุดให้ sink ใหม่ทันที และ detach แล้ว sink เดิมไม่ถูกเรียกอีก
4. `MainWindow` และ `App` เรียกผ่าน `TrayPolicy`/`ApplySettings` จริง (contract test แคบ ๆ เหมือนเฟส 1: ตรวจข้อความในไฟล์ต้นทาง หาไฟล์ root จาก `AIMonitor.sln` ห้ามผ่านเงียบถ้าหาไม่เจอ และต้องยืนยันว่าตรวจอย่างน้อยหนึ่งไฟล์)
5. ผู้ใช้ตรวจบนแอปจริง (ไม่ใช่ agent): เปลี่ยน opacity/topmost แล้ว widget เปลี่ยนทันที; ปิด/เปิด tray icon ใน Settings แล้วไอคอนหายและกลับมาทันที

## ข้อกำหนดของ tests

- ทุก test ต้อง **แดงเมื่อย้อน production code** และต้องรายงาน mutation หนึ่งบรรทัดต่อ test ตามความจริง (Coding Owner จะรันทุกข้อ)
- deterministic: ไม่ใช้ `Task.Delay`/เวลาจริง ใช้ fake sink; ห้าม test ที่ผ่านเพราะ setup ไม่เคยถึงจุดที่ตรวจ
- test ของ `WidgetViewModel` ต้องไม่เปิดหน้าต่างจริง (ใช้ VM ตรง ๆ; `DispatcherTimer` ใน constructor ให้จัดการตามที่ test เดิมของ VM นี้ทำ)

## Commands (Coding Owner รัน ไม่ใช่ Writing Owner ในโหมด headless)

```
dotnet build AIMonitor.sln
dotnet test AIMonitor.sln --no-build
dotnet format AIMonitor.sln --verify-no-changes
```

ค่าฐาน: 966 tests ผ่าน, build 0 warning/0 error

## Constraints

- แก้ไฟล์อย่างเดียว ห้ามรันคำสั่ง (headless ไม่มีสิทธิ์ shell) ห้าม commit; ห้าม `Co-Authored-By:`
- `System.Windows.MessageBox` กับ `System.Windows.Forms.MessageBox` กำกวมในโปรเจกต์ WPF นี้ (มี WinForms ผสม) ให้ระบุเนมสเปซเต็มเสมอ และระวัง `Application` กำกวมแบบเดียวกัน
- เปลี่ยนแปลงให้เล็กที่สุดที่ครบข้อกำหนด

## Open questions / risks

- `TrayIconHost` สร้างบน UI thread เท่านั้น (NotifyIcon/WinForms Timer) ต้องสร้างและ Dispose ใน `OnSettingsChanged` ที่ marshal แล้วเท่านั้น
- การสร้าง/ทำลาย tray ซ้ำหลายครั้งต้องไม่ทิ้ง icon ผี (`NotifyIcon.Visible = false` ก่อน Dispose) — ตรวจโค้ดเดิมของ `TrayIconHost.Dispose`
