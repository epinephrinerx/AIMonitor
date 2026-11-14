# ADR-0002 — Settings เป็น JSON, secrets ผ่าน DPAPI และ migration จาก legacy Registry แบบ read-only

## Status

Accepted — 2026-09-27

## Context

รุ่น Python เดิมเก็บ settings และ credential ไว้ใต้ `HKCU\Software\AIUsageMonitor\AIUsageMonitor`
(QSettings organisation `AIUsageMonitor` + application `AIUsageMonitor`) รวมถึง sub-group ต่าง ๆ ใต้ path
นี้ (ดู `Docs/FEATURE_PARITY.md` PAR-026, PAR-029) รุ่น C# ต้องรักษาค่า intent ของผู้ใช้ (startup, tray,
theme, window size, refresh interval, opacity, topmost, chart range/metric) และต้องปกป้อง saved secret
ด้วย Windows user-bound mechanism โดยห้าม fallback เป็น plaintext (PAR-029) นอกจากนี้ requirement
ห้ามแก้หรือลบ registry values ของรุ่นเดิมระหว่างพัฒนา (`Requirements.md` R-001 "ไม่รวม",
`Docs/FEATURE_PARITY.md` §4)

Secret ของรุ่น Python เดิม (เช่น saved Admin API key) ไม่ได้เก็บเป็น plaintext ใน Registry — รุ่น Python
ใช้ Windows DPAPI scope `CurrentUser` พร้อม application entropy ที่ทราบค่าอยู่แล้วในการเข้ารหัสก่อนเขียน
ลง Registry เช่นกัน ดังนั้น secret เดิมจึง**ปลอดภัยเพียงพอที่จะ decrypt แล้ว migrate ได้** ไม่ใช่ข้อมูลที่
"ไม่ปลอดภัยพอจะ trust" ตามที่ร่างก่อนหน้านี้ระบุไว้

ต้องตัดสินใจสามเรื่อง: รูปแบบ storage ของ settings รุ่นใหม่, กลไกปกป้อง secret และนโยบาย migration
จาก legacy Registry

## Decision

1. **Settings** เก็บเป็นไฟล์ JSON เดียวใต้ `%LOCALAPPDATA%\AIUsageMonitor\settings.json` เขียนผ่าน
   write-temp-then-atomic-replace เพื่อกัน partial write; อ่าน/เขียนผ่าน `ISettingsStore` port ใน
   Application และ implement จริงใน Infrastructure (`JsonSettingsStore`)
2. **Secrets** (เช่น saved Admin API key) เก็บผ่าน **Windows DPAPI scope `CurrentUser`**
   เป็นไฟล์ blob แยกใต้ `%LOCALAPPDATA%\AIUsageMonitor\secrets.dat` ผ่าน `ISecretStore` port;
   ห้ามมี code path ที่เขียน secret เป็น plaintext ไม่ว่ากรณีใด รวมถึง log (ต้อง redact ตาม
   ARCHITECTURE.md §9) การเขียน `secrets.dat` ต้องผ่าน write-temp-then-atomic-replace เช่นเดียวกับ
   `settings.json` (เขียน blob ใหม่ลง temp file ในไดเรกทอรีเดียวกันก่อน แล้ว atomic-replace ไฟล์เดิม)
   เพื่อกัน partial write ทำให้ไฟล์เดิมเสียหาย
3. **Migration จาก legacy Registry เป็นแบบ one-time, อัตโนมัติ (ไม่ต้องผู้ใช้กด opt-in), forward-only
   และ read-only เท่านั้น**:
   - เกิดขึ้นครั้งเดียวโดยอัตโนมัติเมื่อ `settings.json` ยังไม่มีอยู่ (ตรวจจับว่าเป็นการอัปเกรดจากรุ่น
     Python หรือเป็นการติดตั้งใหม่) — ผู้ใช้ไม่ต้องเปิดใช้งาน migration เอง
   - อ่านค่าจาก `HKCU\Software\AIUsageMonitor\AIUsageMonitor` (organisation + application key ตาม
     QSettings เดิม) รวมถึง sub-group/nested key ทั้งหมดใต้ path นี้ ผ่าน `ILegacySettingsReader`
     แล้วแปลงเป็น JSON model เท่านั้น
   - **ห้ามลบหรือเขียนทับค่าใน Registry เดิมไม่ว่าผลลัพธ์ migration จะสำเร็จหรือไม่**
   - ถ้า migration ล้มเหลวบางส่วน (ค่าอ่านไม่ได้/รูปแบบไม่ตรง) ให้ตกไปใช้ default สำหรับค่านั้น
     และบันทึก log แบบไม่ยกระดับเป็น fatal error — แอปต้องเปิดใช้งานได้เสมอ
   - "Forward-only" หมายถึง migration import ค่าจาก legacy Registry เข้า `settings.json` ทิศทางเดียว
     เท่านั้น (Registry → JSON) และรันเฉพาะตอนที่ field นั้นยังไม่มีอยู่ใน `settings.json`; ไม่มี
     sync ย้อนกลับหรือ sync ต่อเนื่องในทิศทางใด (ดูทางเลือกที่ถูกปฏิเสธด้านล่าง)
   - **Secrets ก็ migrate จาก Registry ได้เช่นกัน เพราะ Python เดิมเข้ารหัสด้วย DPAPI `CurrentUser` +
     known application entropy อยู่แล้ว (ดู Context)**: decrypt ค่าเดิมแบบ in-memory เฉพาะตอนรันบน
     Windows user account เดียวกับที่เข้ารหัสไว้ แล้ว re-seal (เข้ารหัสใหม่) เข้า `secrets.dat` ของ
     รุ่น C# ทันที — ห้ามเขียน plaintext ของ secret ลง disk หรือ log ไม่ว่าขั้นตอนใด การ migrate secret
     แต่ละตัวล้มเหลว (decrypt ไม่ได้/รูปแบบไม่ตรง) ต้องเป็น non-fatal ต่อ secret ตัวอื่นและต่อค่า
     Registry เดิม: ข้าม secret นั้นไป ให้ผู้ใช้ sign-in ใหม่เฉพาะ provider นั้น และ**ต้องไม่ลบ/เขียน
     ทับ blob เดิมใน Registry ไม่ว่ากรณีใด**
4. หลัง migration ครั้งแรก `settings.json`/`secrets.dat` เป็น source of truth เพียงแหล่งเดียวสำหรับ
   settings/secret รุ่นใหม่ ระบบจะไม่อ่าน Registry เดิมซ้ำอีก เว้นแต่ผู้ใช้ลบ `settings.json` ทิ้งเอง

## Alternatives considered

- **เขียน settings รุ่นใหม่กลับไปที่ Registry เดิม** — ถูกปฏิเสธเพราะจะเสี่ยงชนกับ instance ของรุ่น
  Python ที่อาจยังติดตั้งอยู่ระหว่าง transition และขัดกับกติกา "ห้ามแก้หรือลบ" ค่าเดิม
- **Migrate แบบต่อเนื่อง (sync ทุกครั้งที่เปิด)** — ถูกปฏิเสธเพราะเพิ่มความซับซ้อนของ conflict
  resolution โดยไม่มีประโยชน์ชัดเจน เมื่อเทียบกับ one-time, automatic, forward-only import ตามที่
  ตกลงไว้ใน Phase 2
- **เก็บ secret ใน DPAPI scope `LocalMachine`** — ถูกปฏิเสธเพราะไม่ผูกกับ Windows user เฉพาะราย
  ขัดกับ PAR-029 ที่ต้องการ per-user protection บนเครื่องที่อาจมีหลาย user account
- **ไม่ migrate secret เลย (ให้ผู้ใช้ sign-in ใหม่ทุกครั้ง)** — ถูกปฏิเสธหลัง verify ว่า secret เดิมของ
  Python ถูก DPAPI `CurrentUser` + known entropy ปกป้องอยู่แล้ว จึง decrypt-in-memory แล้ว re-seal
  ได้อย่างปลอดภัยโดยไม่ต้องให้ผู้ใช้ sign-in ใหม่โดยไม่จำเป็น

## Consequences

- Infrastructure tests ต้องมี fake `ILegacySettingsReader`/fake registry scope เพื่อทดสอบ migration
  โดยไม่แตะ `HKCU` จริงระหว่าง automated test (ดู `Docs/TESTING.md` §5) รวมถึง test ที่ครอบคลุม
  nested key/sub-group ใต้ `HKCU\Software\AIUsageMonitor\AIUsageMonitor`
- ต้องมี explicit test ยืนยันว่า migration ไม่เขียนหรือลบค่า Registry เดิม ทั้ง settings และ secret
  blob (negative test) แม้ตอน migrate secret ตัวใดตัวหนึ่งล้มเหลว
- Settings schema เปลี่ยนแปลงในอนาคตต้องมี versioned migration ภายใน JSON เอง (เช่น field `schemaVersion`)
  เพื่อไม่ผูกกับ legacy Registry migration path ซ้ำสอง
- ผู้ใช้ที่อัปเกรดจากรุ่น Python ที่ secret migrate สำเร็จ ไม่ต้อง sign-in ใหม่; เฉพาะ secret ที่
  migrate ไม่สำเร็จ (เช่น decrypt ไม่ได้) เท่านั้นที่ต้อง sign-in ใหม่สำหรับ provider นั้น
- ต้องมี integration test ยืนยันว่า `secrets.dat` เขียนแบบ atomic (write-temp-then-replace) และไฟล์
  เดิมยังอ่านได้หาก write ถูกขัดจังหวะ (ดู `Docs/TESTING.md` §2)
