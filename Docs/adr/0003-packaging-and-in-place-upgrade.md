# ADR-0003 — Packaging ด้วย Inno Setup self-contained win-x64 และ in-place upgrade จากรุ่น Python

## Status

Accepted — 2026-09-27

## Context

รุ่น Python เดิมแจกจ่ายด้วย PyInstaller (one-file/one-directory) และติดตั้งด้วย Inno Setup
(`Docs/FEATURE_PARITY.md` §1) ผู้ใช้ปัจจุบันมีแอป Python ติดตั้งอยู่แล้วบนเครื่อง และ requirement
ต้องการให้รุ่น C# ทำงานแทนได้โดย runtime หลักไม่พึ่ง Python (`Requirements.md` R-001) รวมถึงต้องรักษา
single-instance และ startup identity ไม่ให้ซ้อนกันระหว่าง process ของสองรุ่น
(`Docs/FEATURE_PARITY.md` PAR-023, PAR-024, PAR-035)

ต้องตัดสินใจ: packaging technology, self-contained vs framework-dependent deployment และนโยบาย
อัปเกรดจากการติดตั้งรุ่น Python เดิม

## Decision

1. **Packaging tool คือ Inno Setup** ต่อเนื่องจากรุ่นเดิม เพื่อคงรูปแบบ installer/uninstaller
   ที่ผู้ใช้คุ้นเคยและลดความเสี่ยงด้าน installer identity
2. **Deployment model คือ self-contained win-x64** (.NET runtime รวมมากับ installer) เพื่อให้ runtime
   หลักไม่ต้องพึ่ง .NET runtime ที่ติดตั้งแยกต่างหาก และไม่ต้องพึ่ง Python runtime ใด ๆ ตาม acceptance
   criteria ของ R-001
3. **Upgrade เป็นแบบ in-place จาก installer รุ่น Python เดิม**:
   - Installer ของรุ่น C# ใช้ **`AppId` เดียวกับรุ่น Python เดิม** เพื่อให้ Windows/Inno Setup มองว่า
     เป็นการอัปเกรดแอปเดียวกัน ไม่ใช่การติดตั้งแบบ side-by-side
   - **การเปลี่ยนผ่านจากรุ่น Python เป็นรุ่น C# เป็นหน้าที่ของ installer เท่านั้น ไม่ใช่ runtime
     detection ข้ามเวอร์ชัน** — รุ่น Python เดิม (Qt/PySide6) ไม่มี named mutex และไม่รู้จักโปรโตคอล
     mutex/pipe ของรุ่น C# เลย (Qt ใช้ local server name รูปแบบ `AIUsageMonitor|<username>|<scope>`
     ผ่าน `QLocalServer`/`QLocalSocket` ซึ่งเป็นกลไกคนละชนิดกับ Win32 named mutex) ดังนั้น
     single-instance coordinator ของรุ่น C# (ADR ตาม `Docs/ARCHITECTURE.md` §6) **ตรวจจับ instance
     ของรุ่น Python ไม่ได้และไม่ต้องพยายามตรวจจับ** — ต้องอาศัย installer หยุด process รุ่น Python
     เดิมอย่างชัดเจนก่อนเขียนไฟล์รุ่นใหม่ (ดู bullet ถัดไป) แทนการพึ่ง mutex/pipe ข้ามเวอร์ชัน
   - Installer ต้องหยุด process ของรุ่น Python เดิมที่กำลังรันอยู่ (เช่น ส่งสัญญาณปิดผ่าน process
     handle ที่ installer ค้นหาด้วยชื่อ/path ของ executable เดิม) ก่อนหรือระหว่างการติดตั้ง เพื่อป้องกัน
     **duplicate startup/tray/poller state** หลังอัปเกรด — ต้องไม่มีทั้งสอง process แข่งกันเปิดพร้อมกัน
     ระหว่าง transition นี้เอง (ไม่ใช่ runtime ของรุ่น C#) ที่รับผิดชอบเปลี่ยน startup entry ตามข้อถัดไป
   - Single-instance coordinator ของรุ่น C# (per-user named mutex + named pipe, ตาม
     `Docs/ARCHITECTURE.md` §6) ใช้ชื่อ mutex/pipe ที่ผูกกับ `AppId` เดียวกันเพื่อให้ instance ของรุ่น
     C# ที่เปิดพร้อมกันหลายตัวตรวจจับกันเองได้สอดคล้องกัน — ขอบเขตนี้ครอบคลุมเฉพาะ instance ของรุ่น
     C# ต่อ C# เท่านั้น ไม่ใช่กลไก interop กับรุ่น Python
   - Legacy Registry values ของรุ่น Python (`HKCU\Software\AIUsageMonitor\AIUsageMonitor`)
     **ต้องไม่ถูกลบโดย installer** — ผู้ใช้ยังสามารถย้อนกลับไปติดตั้งรุ่น Python ได้หากจำเป็น ตามกติกา
     "ห้ามลบข้อมูลรุ่นเดิม" ใน `Requirements.md`/`Docs/FEATURE_PARITY.md`
   - **Startup entry ของรุ่นใหม่ต้องสะท้อนความตั้งใจเดิมของผู้ใช้ ไม่ใช่ลบ/ปิดใช้งานเปล่า ๆ**:
     installer ต้อง snapshot ว่า `HKCU ...\Run` value ของรุ่น Python เดิมมีอยู่หรือไม่ **ทันทีก่อน**
     เริ่มขั้นตอน upgrade แล้วจึงหยุด process เดิม จากนั้น
     - ถ้า value เดิมมีอยู่ (ผู้ใช้เปิด start-with-Windows ไว้) → แทนที่ command ของ value นั้นด้วย
       executable ของรุ่น C# (คง value name เดิมหรือย้ายไปตาม `WindowsStartupRegistrar` ของรุ่นใหม่
       ตามที่ implementation กำหนด) เพื่อให้ผู้ใช้ยังคงได้ start-with-Windows behavior ที่เปิดไว้เดิม
     - ถ้า value เดิมไม่มีอยู่ (ผู้ใช้ปิด start-with-Windows ไว้ใน Windows Startup Apps หรือไม่เคยเปิด)
       → **ห้าม installer สร้าง value นั้นขึ้นมาใหม่** ต้องปล่อยให้ไม่มี startup entry ต่อไปตามที่
       ผู้ใช้ตั้งใจไว้ ไม่ใช่ default เป็นเปิด
     - หลังจากขั้นตอนนี้ Windows Startup Apps ยังคงเป็น source of truth ตาม PAR-024 เหมือนเดิม
4. Versioning: app, executable metadata และ installer ใช้เลขเวอร์ชันจาก **source of truth เดียว**
   ภายใน solution รุ่น C# (รายละเอียด mechanism เป็น implementation detail ของ Infrastructure/build
   script ซึ่งกำหนดตอนสร้าง solution จริง ไม่ใช่ในเอกสาร Phase 2 นี้)
5. **Single-instance mutex และ named pipe ไม่ใช่กลไก access control**: ทั้งสองต้องจำกัดเฉพาะ Windows
   user ปัจจุบัน — named mutex ต้องสร้างด้วย ACL/security descriptor ที่ผูกกับ current user SID เท่านั้น
   (ไม่ใช้ default/null DACL ที่ user อื่นบนเครื่องเดียวกันอาจเปิด/signal ได้) และ named pipe ต้องสร้าง
   แบบ current-user-only (เช่น `PipeOptions`/`PipeSecurity` ที่จำกัด `FILE_ALL_ACCESS` ให้เฉพาะ
   current user SID และ deny remote client) ต้องมี negative integration test ยืนยันว่า user/session
   อื่นเปิดหรือ activate ผ่าน mutex/pipe ชื่อเดียวกันไม่ได้ เท่าที่ทดสอบได้ในสภาพแวดล้อม CI

## Alternatives considered

- **Framework-dependent deployment** — ถูกปฏิเสธเพราะเพิ่ม dependency ให้ผู้ใช้ต้องติดตั้ง .NET
  runtime แยก ซึ่งขัดกับเป้าหมายที่ต้องการให้ upgrade จาก Python ราบรื่นและไม่มี prerequisite เพิ่ม
- **MSIX/Store packaging** — ถูกปฏิเสธเพราะ MSIX ผูก identity/sandboxing คนละแบบกับ Inno Setup เดิม
  จะทำให้ "in-place upgrade preserving legacy AppId" ทำได้ยากหรือเป็นไปไม่ได้ในบาง Windows edition
- **Side-by-side install (AppId ใหม่)** — ถูกปฏิเสธเพราะจะทำให้มีสอง process/tray icon/startup entry
  พร้อมกัน ขัดกับ PAR-023/PAR-024 และสร้างความสับสนให้ผู้ใช้

## Consequences

- Installer script (Inno Setup, ยังไม่สร้างในเฟสนี้) ต้องมีขั้นตอน pre-install ที่:
  1. Snapshot สถานะ `HKCU ...\Run` value ของรุ่น Python เดิมก่อนแตะสิ่งใด
  2. หยุด process รุ่น Python เดิม
  3. เขียนไฟล์รุ่นใหม่ทับ
  4. เขียน/ปล่อยว่าง startup entry ของรุ่นใหม่ตามผลลัพธ์จาก snapshot ในขั้นตอนที่ 1 (ดู Decision ข้อ 3)
  — ไม่ใช่ "ปิด startup entry เดิมอย่างชัดเจน" แบบไม่มีเงื่อนไขตามที่ร่างก่อนหน้านี้ระบุไว้
- ต้องมี manual/smoke test เฉพาะสำหรับ upgrade path ทั้งสองกรณี: (a) รุ่น Python เดิมเปิด
  start-with-Windows ไว้ → หลังอัปเกรดต้องเปิดอยู่และชี้ไป executable ของรุ่น C# และ (b) รุ่น Python
  เดิม**ปิด**ไว้ → หลังอัปเกรดต้องยังปิดอยู่ (ติดตั้งรุ่น Python เดิม → รันจริง → อัปเกรดด้วย
  installer รุ่น C# → ยืนยันไม่มี duplicate tray/startup/poller) ตาม `Docs/TESTING.md` §8
- Self-contained win-x64 ทำให้ขนาด installer ใหญ่ขึ้นเทียบกับ framework-dependent แต่แลกกับ
  ความแน่นอนของ runtime บนเครื่องผู้ใช้
- Rollback plan เมื่ออัปเกรดล้มเหลว: เก็บ installer ของเวอร์ชันก่อนหน้าไว้เพื่อ re-install ได้
  (ดู `Docs/TESTING.md` §11) เนื่องจาก Registry/legacy data ของรุ่น Python ไม่ถูกลบ
- Infrastructure test ของ single-instance coordinator ต้องยืนยัน ACL/security scope ของ mutex/pipe
  ผูกกับ current user SID เท่านั้น (ดู Decision — IPC ไม่ใช่ access control โดยตัวชื่อเพียงอย่างเดียว)
