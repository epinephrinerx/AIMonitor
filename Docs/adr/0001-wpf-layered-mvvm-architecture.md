# ADR-0001 — WPF บน .NET 10 กับ layered architecture + MVVM

## Status

Accepted — 2026-09-27

## Context

`R-001` ต้องย้าย AIMonitor จาก Python/PySide6 เป็น C# สำหรับ Windows โดยรักษาความสามารถหลักตาม
`Docs/FEATURE_PARITY.md` (PAR-001 ถึง PAR-036) ซึ่งรวม dashboard แบบเต็มและ widget mode ที่ resize/pin ได้,
system tray ที่วาด icon จาก usage สด, custom gauges/แผนภูมิ, always-on-top/opacity และ geometry ต่อ display/DPI
แอปเป็น desktop tool ผู้ใช้เดียวต่อเครื่อง ไม่มี public API หรือ multi-tenant boundary ดังนั้นความซับซ้อนที่ต้องเลือกคือ
UI framework, การจัดชั้นโค้ด (layering) และรูปแบบ UI pattern ที่ทำให้ business rule (parity, severity, pricing)
ทดสอบได้แยกจาก UI

ทางเลือกที่พิจารณา:

- **WinUI 3** — modern control set และ Fluent design แต่ ณ วันที่ตัดสินใจยังมีข้อจำกัดเรื่อง packaging
  (มักผูกกับ MSIX หรือ sparse package), custom low-level drawing/tray integration ยังไม่นิ่งเท่า WPF/WinForms
  และมีความเสี่ยงด้าน tooling/versioning สำหรับ self-contained win-x64 ที่ต้องใช้ Inno Setup
- **WinForms** — เรียบง่ายและมี `NotifyIcon` ในตัว แต่ custom-drawn gauges/charts และ MVVM binding ทำได้จำกัดกว่า
  WPF และจะเพิ่มงาน custom control สำหรับ dashboard ที่ซับซ้อน
- **WPF** (เลือก) — มี data binding/`ICommand`/`INotifyPropertyChanged` ที่รองรับ MVVM โดยตรง, custom drawing
  ผ่าน `DrawingContext`/`Visual` เพียงพอสำหรับ gauges และ daily chart, DPI/multi-monitor API ที่โตเต็มที่
  สำหรับ geometry ต่อ display (PAR-021), และใช้ `System.Windows.Forms.NotifyIcon` ตรง ๆ สำหรับ tray icon
  ได้โดยไม่ต้องเขียน tray ใหม่ทั้งหมด (ไม่ต้องพึ่ง `WindowsFormsIntegration` เพราะไม่มีการ host WinForms
  control ใด ๆ ภายใน WPF visual tree — ดู Decision ข้อ 6)

## Decision

1. Runtime เป้าหมายคือ **.NET 10 LTS สำหรับ Windows Desktop**
2. UI framework คือ **WPF**
3. Architecture คือ **layered architecture + MVVM** แบ่งเป็นสี่ชั้น:
   - `Domain` — entities/value objects และ business rule ล้วน (severity thresholds, pricing tiers,
     detection status, ordering ของ quota window) ไม่ผูกกับ framework, network client หรือ UI
   - `Application` — use cases/orchestration (เช่น refresh provider, build report, run migration)
     และ port interfaces (เช่น `IUsageProviderClient`, `ISettingsStore`, `ISecretStore`,
     `IStartupRegistrar`, `ISingleInstanceCoordinator`) ที่ชั้นนอกต้อง implement
   - `Infrastructure` — adapter จริงที่ implement ports ของ Application: HTTP/process client ต่อ provider,
     JSON settings store, DPAPI secret store, legacy registry reader, Windows startup/tray/single-instance
   - `Presentation (WPF)` — Views, ViewModels (MVVM), composition root ที่ประกอบ DI container
4. Dependency direction: **Presentation → Application → Domain**; **Infrastructure implement
   inward-facing ports ที่ Application ประกาศไว้** (Infrastructure ไม่ถูกเรียกตรงจาก Presentation
   ยกเว้นจุดเดียวคือ composition root ใน `App.xaml.cs`/`Program.cs` ที่ต้องอ้างถึง Infrastructure
   เพื่อลงทะเบียน DI เท่านั้น ไม่ใช่เพื่อเรียก business logic)
5. Domain ต้องไม่ import WPF, `System.Net.Http`, registry API หรือ filesystem API โดยตรง
6. Tray icon ใช้ `System.Windows.Forms.NotifyIcon` โดยตรงเป็น component หนึ่งของ WPF process (reference
   assembly `System.Windows.Forms` เฉพาะจุดที่สร้าง/ควบคุม tray icon เท่านั้น) — ไม่ต้องใช้
   `WindowsFormsIntegration` เพราะไม่ได้ host WinForms control ใด ๆ ไว้ใน WPF visual tree; ถือเป็น
   ส่วนหนึ่งของ Infrastructure/Presentation composition ไม่ใช่ Domain/Application หากในอนาคตมีการ
   host WinForms control จริง (เช่น embed WinForms UserControl ใน WPF window) ต้องเพิ่ม
   `WindowsFormsIntegration` และปรับ ADR นี้ตามนั้น

## Alternatives considered

ดูหัวข้อ Context — WinUI 3 และ WinForms ถูกปฏิเสธด้วยเหตุผลด้าน packaging risk และ custom-drawing/MVVM
ergonomics ตามลำดับ

## Consequences

- Business rule (parity PAR-005, PAR-007, PAR-016 เป็นต้น) ทดสอบได้ด้วย unit test ล้วนใน `Domain`/`Application`
  โดยไม่ต้องเปิด WPF runtime
- ต้องเขียน custom-drawn control เองสำหรับ gauges/daily chart (ไม่มี built-in chart library ที่อนุมัติ)
  ซึ่งเพิ่มต้นทุนพัฒนา/บำรุงรักษาเทียบกับ chart library สำเร็จรูป
- การอ้างอิง `System.Windows.Forms.NotifyIcon` จาก WPF process เพิ่ม assembly reference ข้าม UI stack
  หนึ่งจุด แต่จำกัดผลกระทบไว้ที่ tray-hosting component เดียวใน Infrastructure/Presentation และไม่ต้อง
  ผูกกับ `WindowsFormsIntegration`
- Single-instance และ startup ใช้ Win32/registry API ตรงผ่าน Infrastructure adapters ตาม ADR-0002/ADR-0003
