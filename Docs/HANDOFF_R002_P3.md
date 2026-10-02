# Handoff — R-002 เฟส 3: Connections page, ConnectDialog และเส้นทางเขียน secret

Coding Owner (Claude Code) → Writing Owner (Antigravity) · branch `agent/antigravity-connections` · ฐาน `feat/csharp-rewrite`
แบ่ง 2 รอบ: **3a** ชั้น Application + wiring (ไม่มี UI) แล้ว **3b** UI

## ปัญหาที่พิสูจน์จากโค้ด

- `App.OnStartup` อ่าน `providers/openai/key`, `providers/gemini/key` และ `ProviderPreference.Extra` **ครั้งเดียวตอนเริ่ม** แล้วส่งเข้า constructor ของ
  `OpenAiLiveQuotaClient`/`GeminiLiveQuotaClient` ดังนั้นต่อให้มี UI ใส่ key การเปลี่ยนจะไม่มีผลจนรีสตาร์ท
- ไม่มีโค้ดไหนเขียน `secrets.dat` หลัง migration (`ISecretStore.SetAsync/RemoveAsync` ไม่มีผู้เรียก)
- ไม่มีหน้า Connections / Re-detect / ConnectDialog (PAR-014 ใน 1.3.3 มี) ทั้งที่ `ProviderSnapshot.Detection` มีข้อมูลครบ

## รอบ 3a — Application + wiring (ไม่แตะ XAML)

ใหม่ใน `src/AIMonitor.Application/Providers/` (หรือ `Settings/` ตามความเหมาะสม) ไม่พึ่ง WPF/Infrastructure:

1. `ProviderConnectionStore(ISecretStore secrets, SettingsSession session)`
   - `Task<ProviderConnection> GetAsync(string providerId, ct)` → `ProviderConnection(string? Key, string Extra)`; key มาจาก secret ชื่อ `providers/{id}/key`, extra จาก `session.Current.Providers[id].Extra`
   - `Task SaveAsync(string providerId, string? typedKey, bool clearRequested, string? extra, ct)` ความหมาย 3 ทางของ 1.3.3:
     typedKey ไม่ว่าง (หลัง Trim และ Trim `"`) = แทนที่ · typedKey ว่าง + clearRequested = ลบ · ว่างเฉย ๆ = ไม่แตะ key
     extra: `null` = ไม่แตะ, ค่าอื่น = Trim แล้วบันทึก (ว่าง = ล้าง)
   - ลำดับ: เขียน secret ก่อน; ถ้า secret ล้ม โยน exception และ **ไม่แตะ settings**; จากนั้น `session.UpdateAsync(s => ...)` เปลี่ยนเฉพาะ `Providers[id]` (คง `Enabled` เดิม) — ห้ามเขียนทับ field อื่น
   - เมื่อสำเร็จยิง event `Changed(string providerId)`
   - provider id ที่ไม่รู้จัก (ไม่ใช่ claude/openai/gemini) → `ArgumentException`; claude ไม่มี key (ไม่รับ typedKey; อนุญาต extra ว่างเท่านั้น หรือโยนก็ได้ ให้ระบุ)
2. `ReconfigurableQuotaClient : IProviderQuotaClient`
   - ctor: `(ProviderConnectionStore store, string providerId, Func<ProviderConnection, IProviderQuotaClient> factory)`
   - ทุก `GetSnapshotAsync` อ่าน `store.GetAsync` ล่าสุด; ถ้า (Key, Extra) เปลี่ยนจากครั้งก่อน ให้สร้าง inner client ใหม่ด้วย factory (cache ตาม key+extra, ไม่สร้างซ้ำทุกครั้ง); inner ที่เป็น `IDisposable` ให้ dispose ตัวเก่า
   - ส่ง `CancellationToken` ต่อ; อย่ากลืน `OperationCanceledException`
3. `App.xaml.cs`: ลบการอ่าน key/pref ครั้งเดียว สร้าง `ProviderConnectionStore` แล้วลงทะเบียน openai/gemini ผ่าน `ReconfigurableQuotaClient` โดย factory
   สร้าง `OpenAiLiveQuotaClient`/`GeminiLiveQuotaClient` เดิมจาก (Key, Extra) (budget/projectOverride แปลงเหมือนเดิมใน `App`) ส่วน claude คงเดิม เปิดเผย `ProviderConnectionStore` ให้เฟส 3b
   ห้ามแก้ constructor ของ client ใน Infrastructure และห้ามแก้ test ของมัน

### tests ของ 3a (ต้องแดงเมื่อย้อน production)
- ใช้ `JsonSettingsStore` จริง + `SettingsSession` จริง; secret store ใช้ fake in-memory ที่ **ล้มได้ตามสั่ง** (ห้ามบางจนไม่วิ่งผ่านตรรกะ)
- SaveAsync: แทนที่/ลบ/ไม่แตะ ทั้ง 3 ทาง; clear แล้วพิมพ์ใหม่ = แทนที่; secret ล้ม → settings ไม่เปลี่ยน; ไม่ทับ field อื่น (เช่น theme ที่เปลี่ยนระหว่างนั้น); Changed ยิงเฉพาะเมื่อสำเร็จ
- ReconfigurableQuotaClient: เปลี่ยน key ระหว่าง 2 ครั้งเรียก → factory ถูกเรียก 2 ครั้งด้วยค่าใหม่; ค่าเดิม → factory ครั้งเดียว; inner ตัวเก่าถูก dispose
- ห้ามใช้ key จริง/network จริง/registry จริง

## รอบ 3b — UI (หลัง 3a ผ่านเกณฑ์) — รายละเอียดจะส่งเป็น handoff แยก

ไม่ทำในรอบ 3a: ConnectionsViewModel/การ์ด/ConnectDialog/ปุ่ม Re-detect/ปุ่ม Connections บน header

## Non-goals / Constraints
- ไม่ push/merge · commit trailer `Assisted-by: Antigravity (Google)` ห้าม `Co-Authored-By:`
- ห้ามเพิ่ม NuGet · ห้ามแก้ schema `AppSettings` · ห้ามแตะ `DpapiSecretStore` · ถ้ากติกาขัดกับโค้ดจริงให้หยุดรายงาน
- build 0 warning, `dotnet format` สะอาด, tests เดิมต้องไม่แตก

---

# รอบ 3b — UI (3a ผ่านแล้ว: commit 0148147)

แบ่งเป็น **3b-1 ViewModels + tests (ไม่มี XAML)** แล้ว **3b-2 XAML + wiring** ทำ 3b-1 ก่อน

อ้างอิงต้นฉบับ 1.3.3 (อ่านอย่างเดียว): `D:\Dev\Apps\AIMonitor\ai_usage_monitor\connections_page.py`, `connect_dialog.py`, `providers\*_provider.py` (metadata, setup hint), `secrets.py` (`mask`)

## 3b-1 — ใน `src/AIMonitor.Presentation.Wpf/ViewModels/`

1. `ProviderMeta` (record + static รายการ claude/openai/gemini): `Id, DisplayName, BadgeLetter, Tagline, NeedsKey, KeyLabel, KeyPlaceholder, ExtraLabel, ExtraPlaceholder, SetupHint`
   ค่าตามไฟล์ Python ข้างบนคำต่อคำ (display name ให้ตรงกับ tab ปัจจุบัน: "Claude", "OpenAI / Codex", "Gemini"; SetupHint เป็นข้อความธรรมดา ไม่มี HTML tag — แปลง `<b>`/`<code>`/`<br><br>` เป็นข้อความ/ย่อหน้า)
2. `ConnectionCardViewModel(ProviderMeta)` + `Update(DetectionInfo? detection)`:
   - `StateWord`: Connected / Limited / Expired / Not connected · `StateGlyph`: ✓ / ! / ! / – (สถานะต้องไม่พึ่งสีอย่างเดียว)
   - `Account`: detection.Account ถ้ามี; ไม่มีและสถานะ NotConnected → "Not signed in"; อื่น ๆ → Tagline
   - `SourceLine`: ส่วนที่มี ต่อด้วย `"  -  "`: `Detected from {SourceLabel}` (ถ้า SourceLabel ไม่ว่าง) และ Hint; ถ้าไม่มีอะไรเลย → Tagline
   - `ConnectButtonText`: NeedsKey=false → "Sign-in help"; ไม่งั้น Connected → "Change..." อื่น ๆ → "Connect..."
   - `Detection` null (ยังไม่ refresh) = ปฏิบัติเหมือน NotConnected แต่ `StateWord` เป็น "Checking..."
   - commands `ConnectCommand`, `RedetectCommand` ยิง event `ConnectRequested(providerId)`/`RedetectRequested(providerId)`
3. `ConnectionsViewModel`: `Cards` (3 ใบเรียง claude, openai, gemini), `Update(IReadOnlyDictionary<string, ProviderSnapshot>)` ใช้ `snapshot.Detection` ของแต่ละ id, `ShowAtStartup` (อ่าน/เขียน `AppSettings.ShowConnectionsAtStartup` ผ่าน `SettingsSession.UpdateAsync` เปลี่ยนเฉพาะ field นี้ ห้ามทับ field อื่น), commands `RedetectAllCommand`, `OpenDashboardCommand`; events `ConnectRequested(string)`, `RedetectRequested(string?)`, `OpenDashboardRequested`
4. `ConnectDialogViewModel(ProviderMeta meta, DetectionInfo? detection, ProviderConnection existing, ProviderConnectionStore store)`:
   - อ่านอย่างเดียวจนกว่าจะ Save: **Cancel/Close ห้ามเรียก `store` เด็ดขาด**; `ClearCommand` แค่ตั้ง `ClearRequested = true`, ล้าง `Key`, placeholder เป็น `(cleared when you save)`
   - `KeyPlaceholder` = `Mask(existing.Key)` ถ้ามี key เดิม ไม่งั้น `meta.KeyPlaceholder` (Mask: ว่าง → ""; ยาว ≤ 12 → "•" × ความยาว; ไม่งั้น `secret[:8] + "…" + secret[-4:]`)
   - `SaveCommand` → `await store.SaveAsync(meta.Id, Key, ClearRequested, meta.ExtraLabel is empty ? null : Extra)` แล้ว `RequestClose(true)`; ถ้า `IOException`/`UnauthorizedAccessException`/`CryptographicException` → ยิง `SaveFailed(message)` และ **ไม่ปิด dialog** (ไม่ log ค่า key); พิมพ์ key หลัง Clear = แทนที่ (store จัดการ)
   - `NeedsKey=false` (Claude): ไม่มี field, ปุ่มเดียว Close; `SaveCommand` ไม่ทำอะไร
   - `IsKeyRevealed` toggle (Show), `BrowseCommand` ยิง `BrowseRequested` (view เปิด file picker แล้วเรียก `SetKeyFromBrowse(path)`) แสดงเฉพาะ provider ที่ `KeyPlaceholder` มี "json" (gemini)
   - ส่วน "Found on this machine": `FoundLine` (`NotConnected` → "No existing sign-in found for this service."; อื่น ๆ → `{glyph}  {SourceLabel}{ — Account}  ·  {StateWord}`), `Hint`, `AlsoPresent` ("Also present: a, b" จาก Candidates ที่ SourceId ต่างจาก detection.SourceId; ว่างถ้าไม่มี)
5. `MainWindowViewModel`: เพิ่ม `IsConnectionsPageVisible` (set ผ่าน `ShowConnectionsCommand`/`ShowDashboardCommand`), property `Connections` (ConnectionsViewModel) อัปเดตทุกครั้งที่ `RefreshAsync` ได้ snapshot, event `RequestConnect(string providerId)` ต่อจาก `Connections.ConnectRequested`, `Redetect*` → `RefreshAsync()` (re-detect = refresh รอบเดียวทุก provider; ระบุใน comment); ค่าเริ่มต้นของ `IsConnectionsPageVisible` = `settings.ShowConnectionsAtStartup` ตอนสร้าง ctor ไม่ต้องเพิ่ม param ใหม่ถ้าอ่านจาก `_settingsSession` ได้
   - ห้ามแตะ XAML และ code-behind ในรอบนี้

### tests ของ 3b-1 (ต้องแดงเมื่อย้อน production; ห้าม fake บางจน test ผ่านเพราะเหตุผลผิด)
ใช้ `ProviderConnectionStore` จริง + `SettingsSession` จริง (settings store ไม่บล็อก; secret fake ที่ล้มได้และบันทึกการเรียก)
- Clear แล้ว Cancel → ไม่มีการเรียก secret store และ key เดิมยังอยู่; Clear แล้ว Save → ลบ; Clear แล้วพิมพ์ใหม่ Save → แทนที่; ว่างเฉย ๆ Save → key ไม่ถูกแตะ; Save ล้ม → dialog ไม่ปิด + `SaveFailed` ยิง + ข้อความไม่มีค่า key; extra ถูกส่งเฉพาะ provider ที่มี extra
- `Mask` ทุกกรณี (ว่าง, ≤12, >12); `KeyPlaceholder` ไม่เผยค่า key เต็ม
- การ์ด: 4 สถานะ × glyph/word/Account/SourceLine/ConnectButtonText รวม Claude "Sign-in help" และ detection null
- `ConnectionsViewModel.Update` จับคู่ id ถูกตัว ไม่สลับ; `ShowAtStartup` เขียนเฉพาะ field นั้น (เปลี่ยน theme ระหว่างนั้นต้องไม่หาย)
- `MainWindowViewModel`: refresh แล้ว `Connections` ได้ detection ล่าสุด; Redetect เรียก refresh
- ห้ามใช้ key จริง/network/registry จริง; ห้าม Task.Delay

## 3b-2 (ส่งแยกหลัง 3b-1): `ConnectionsPage` UserControl, `ConnectDialog` window, ปุ่ม Connections บน header, สลับหน้า, `App` เปิด dialog แล้ว refresh หลัง Save
