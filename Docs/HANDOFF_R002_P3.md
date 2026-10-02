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
