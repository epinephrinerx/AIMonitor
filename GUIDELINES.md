# แนวทางทำงานร่วมกันในโปรเจกต์ AI Usage Monitor

อ่าน [README.md](README.md) สำหรับพฤติกรรมโปรแกรม สถาปัตยกรรม และวิธี build
ไฟล์นี้บันทึกข้อกำหนดและบริบทสำหรับ Claude Code, Codex และผู้พัฒนาที่รับงานต่อ

นี่คือต้นฉบับเพียงไฟล์เดียว `CLAUDE.md` กับ `AGENTS.md` เป็นเพียงตัวชี้มาที่ไฟล์นี้
เพราะเครื่องมือแต่ละตัวมองหาชื่อของตัวเอง แก้ที่นี่ที่เดียวพอ

## โปรเจกต์ที่ต้องแก้

- แอป Windows ใช้ Python + PySide6 มี Dashboard, Widget และ System tray
- ซอร์สหลักคือ `ai_usage_monitor/`; entry point คือ `run_ai_monitor.py`
- `run_ai_monitor.py` เป็น entry point เดียว โปรแกรมรุ่นเก่า `claude_monitor/` กับ `run_app.py` ถูกลบออกจาก repo แล้ว ยังกู้ได้จาก history ถ้าจำเป็น
- ใช้ `.venv\Scripts\python.exe` และ `AIUsageMonitor.spec` สำหรับ portable build
- ตรวจ `git status` ก่อนแก้ และรักษางานที่ยังไม่ได้ commit ของผู้ใช้หรือผู้ช่วยอื่น

## ข้อกำหนด OpenAI ที่ผู้ใช้ต้องการ

แสดงโควตาจากการล็อกอิน Codex ให้คล้าย Claude: เปอร์เซ็นต์การใช้และเวลารีเซ็ต
พร้อมประวัติ token เท่าที่บริการส่งกลับมา

1. `OpenAIProvider.detect()` ต้องเลือก Codex ChatGPT OAuth ก่อน Admin key ที่บันทึกไว้หรืออยู่ใน environment
2. หากพบ OAuth แต่หมดอายุหรือไม่สมบูรณ์ ให้แสดงคำแนะนำแก้การล็อกอิน ห้ามสลับไปแสดงค่าใช้จ่าย API โดยเงียบ ๆ
3. หากไม่พบ Codex ChatGPT OAuth จึงใช้ลำดับเดิม: saved key → environment key → CLI API key
4. เก็บ Admin key เดิมไว้ ไม่ต้องลบ key หรือรีเซ็ต Registry เพื่อเปิดใช้โควตา Codex
5. Ordinary project keys ใช้อ่าน organization spend ไม่ได้ ต้องแสดงสถานะ Limited

`providers/sources.py` เก็บรายการแหล่งข้อมูลตามลำดับทั่วไป ส่วนข้อยกเว้น Codex-first
อยู่ใน `OpenAIProvider.detect()` โดยตั้งใจ อย่าย้ายกลับไปใช้ `resolve()` ทั่วไปอย่างเดียว

## การดึงข้อมูลและข้อจำกัด

- `providers/sources.py`: อ่าน `CODEX_HOME/auth.json` หรือ `~/.codex/auth.json`
- `codex_usage.py`: เรียก Codex App Server ผ่าน stdin/stdout ในโปรเซสซ่อนหน้าต่าง
- `account/rateLimits/read`: อ่านเปอร์เซ็นต์ ช่วงเวลา และเวลารีเซ็ตจริง รวม limit groups เพิ่มเติม
- `account/usage/read`: อ่านสรุปและ daily total tokens ที่มีให้
- ไม่สมมติว่าทุกบัญชีมีช่วงเวลา 5 ชั่วโมง/7 วัน ต้องใช้ค่าจากเซิร์ฟเวอร์
- ประวัติในเส้นทางนี้ยังไม่มี output-token, model/project หรือ dollar breakdown; กราฟรองรับ Total tokens
- ห้ามสร้างเปอร์เซ็นต์จาก token เอง ห้ามเติมวันขาดหายเป็นศูนย์ และห้ามทำโควตาหายเมื่อ history ล้มเหลว
- ใช้ temporary `CODEX_HOME` แยกจากโปรไฟล์จริง ไม่คัดลอก config หรือ refresh token
- ส่ง access token ผ่าน stdin เท่านั้น ไม่ใส่ใน command arguments, log, exception หรือเอกสาร
- ไม่แก้ไฟล์ล็อกอินและไม่รีเฟรช token ของ CLI; ให้ผู้ใช้เปิด Codex เพื่อทำวงจรนี้เอง
- ปิดโปรเซสและ pipe หลังดึงข้อมูลเสร็จหรือผิดพลาด จำกัดเวลารอ และไม่สร้าง thread/turn หรือส่ง prompt
- การยืนยันตัวตนแบบ externally supplied tokens ยังเป็น experimental ตรวจเอกสารและ schema ของ Codex ก่อนเปลี่ยน protocol
- เอกสารอ้างอิง: [Codex App Server](https://learn.chatgpt.com/docs/app-server)

## บันทึกการแก้ไข 2026-09-14

หลังเพิ่ม Codex quota ผู้ใช้รายงานว่าไม่มีข้อมูล หน้าจอ Connections ของโปรแกรมจริง
แสดง `Detected from Admin key saved in this app`: key ที่บันทึกไว้มีลำดับสูงกว่า Codex
จึงเข้าโหมด API spend การทดสอบ provider โดยไม่โหลดค่าตั้งเดิมไม่ครอบคลุมกรณีนี้

แก้ด้วย Codex-first detection และเพิ่ม regression tests สำหรับ saved/environment Admin key,
OAuth หมดอายุ และ Admin fallback เมื่อไม่มี OAuth โดยไม่ลบค่าตั้งเดิม

ผลตรวจ ณ วันดังกล่าว:

- ชุดทดสอบ 14 กรณีผ่าน
- เปิด **ไฟล์ .exe ที่ build จริง** และยืนยันบนแท็บ OpenAI ว่าเกจ Session/Weekly เวลารีเซ็ต
  และกราฟรายวันขึ้นแล้ว ไม่ได้ตรวจเฉพาะการรัน Python จากซอร์ส
- ไฟล์ที่ตรวจผ่านคือ `dist/codex-first/AIUsageMonitor.exe`
- `dist/openai-quota/AIUsageMonitor.exe` เป็น build ก่อนแก้ลำดับบัญชี
- ตัวติดตั้งและทางลัดเดิมยังไม่ได้อัปเดตในการแก้ครั้งนี้ อย่าถือว่าเปิด Start Menu แล้วจะเป็น build ล่าสุด
- เปอร์เซ็นต์ที่เห็นตอนทดสอบเป็นข้อมูลชั่วขณะ ไม่ใช่ค่าคาดหวังสำหรับ tests

## ทดสอบและ build

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
.\.venv\Scripts\python.exe -m ai_usage_monitor
.\.venv\Scripts\python.exe -m PyInstaller AIUsageMonitor.spec --noconfirm --distpath dist/codex-first --workpath build/codex-first
```

ทดสอบกรณีมี Admin key เดิมควบคู่กับ Codex login ทุกครั้งที่เปลี่ยน detection
ใช้ข้อมูลจำลองสำหรับ automated tests และอย่าแสดงข้อมูลลับเมื่อวินิจฉัยเครื่องจริง

ก่อนลองไฟล์ build ใหม่ ให้ Exit ตัวเดิมจาก Tray และตรวจ executable path ของโปรเซส
ระบบ single-instance อาจเปิดหน้าต่างของรุ่นเก่าแทนไฟล์ที่เพิ่งเรียก โปรแกรม portable แบบ one-file
อาจมี parent/child สองโปรเซสจาก PyInstaller ซึ่งไม่ได้แปลว่ามี monitor สองหน้าต่าง

การ build portable ไม่ได้อัปเดตตัวติดตั้ง หากต้องออกรุ่นติดตั้ง ให้ทำตาม README
และตรวจ `build_ai_installer.ps1` ก่อนใช้ เพราะสคริปต์ปิด monitor และล้าง build outputs
เมื่อเปลี่ยนเวอร์ชันต้องรักษา installer `AppId` เดิมไว้

## บันทึกการแก้ไข 2026-09-14 (รอบที่สอง) — เมนูบาร์ รายงาน และ Connections ที่ค้าง

ผู้ใช้รายงานว่า "ไม่มีการอัพเดทข้อมูลการเชื่อมต่อ" ทำซ้ำได้จริง: worker เรียก
`provider.detect()` ใหม่ทุกรอบ refresh และใส่ผลไว้ใน `snapshot.detection` ครบทุกเส้นทาง
แต่ `MainWindow._on_result()` ไม่เคยอ่านค่านั้น การ์ดบนหน้า Connections จึงค้างที่ค่าตอน
เปิดหน้า จนกว่าจะกด Re-detect เอง

แก้โดยเพิ่ม `detection.adopt()` แล้วเรียกจาก `_on_result()` ทดสอบใน
`tests/test_connections_freshness.py` ซึ่งมี test เชิงสัญญาที่ parse ซอร์สของ provider
ทั้งสามด้วย AST เพื่อยืนยันว่าทุกจุดที่สร้าง `ProviderSnapshot` ยังส่ง `detection` มาด้วย
ถ้าเพิ่มเส้นทาง fetch ใหม่แล้วลืมใส่ `detection=` test นี้จะจับได้

### สิ่งที่เพิ่มในรอบนี้

- เมนูบาร์ File / Settings / About; ย้าย Connections, Settings, Readme และปุ่ม Theme
  ออกจาก header เหลือ metric / range / interval / Widget / Refresh
  (ผู้ใช้ขอให้เก็บปุ่ม Widget ไว้ที่ header เพราะใช้บ่อย ทั้งสองปุ่มมีในเมนูด้วย
  ซึ่งเป็นที่ประกาศ shortcut อย่าเอาออกจาก header อีก)
- `report.py` โมเดลรายงานเดียว render ได้สามแบบ (HTML บนจอและสำหรับพิมพ์, CSV, Markdown)
- `log_dialog.py` หน้าต่าง Usage log แบบ modeless พร้อม Save as… และ Print preview
- `about_dialog.py` Version + ตรวจอัปเดตจาก GitHub (ทำงานบน QThreadPool ไม่บล็อก GUI)
  และหน้าผู้พัฒนา
- `readme_dialog.py` ขยายเป็น `DocumentDialog` ใช้ร่วมกับ LICENSE และ THIRD-PARTY-NOTICES
- `settings_dialog.py` opacity เป็น slider พร้อม swatch พรีวิว และพรีวิวสด
  (theme / opacity / always-on-top / ขนาดหน้าต่าง) โดย Cancel คืนค่าเดิมทุกตัว
- `version.py` เวอร์ชันมีแหล่งเดียวคือ `__init__.__version__`; `tests/test_version.py`
  จะ fail ถ้า `version_info.txt` หรือ `installer/AIUsageMonitor.iss` ไม่ตรงกัน

### ข้อกำหนดที่ตั้งใจไว้ อย่าเปลี่ยนโดยไม่ตั้งใจ

- รายงานสร้างจาก snapshot ที่แดชบอร์ดถืออยู่แล้ว **ห้ามยิง fetch ของตัวเอง** มิฉะนั้น
  เอกสารที่พิมพ์จะไม่ตรงกับหน้าต่างที่สั่งพิมพ์
- รายงานใช้ metric ที่ผู้ใช้เลือกอยู่ และเขียนชื่อ metric ไว้ในหัวตาราง ห้ามสมมติว่าเป็น token
- พิมพ์ด้วย palette สว่างเสมอ (`to_html(dark=False)`) ธีมมืดต้องไม่ทำให้พิมพ์ออกมาดำทั้งหน้า
- บริการที่ไม่มีประวัติรายวันต้องขึ้นหมายเหตุ ห้ามเติมวันเป็นศูนย์ และบริการที่ fetch ล้มเหลว
  ต้องยังมีหัวข้อพร้อมเหตุผล ห้ามหายไปเงียบ ๆ
- ตรวจอัปเดตเป็น GET เดียวแบบไม่ยืนยันตัวตน ไม่ดาวน์โหลดและไม่ติดตั้งอะไร
  ถ้า GitHub ตอบ 404 ทั้งสอง endpoint ให้บอกว่ามองไม่เห็น repo ห้ามรายงานว่า "up to date"
- พรีวิวสดของ Settings ต้องไม่แตะเครือข่ายหรือ worker (`_apply_appearance`)
  ส่วน `_on_settings_changed` เท่านั้นที่ push credentials แล้ว refresh

## ข้อควรระวังตอนรันจากซอร์ส

`startup.reconcile()` ทำงานใน `MainWindow.__init__` และ **เขียนทับ** ค่า
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AIUsageMonitor` ให้ชี้มาที่
`launch_command()` ของโปรเซสปัจจุบัน การรัน `python -m ai_usage_monitor` บนเครื่องที่
ติดตั้งรุ่น .exe ไว้แล้วจึงทำให้ทางลัด startup ชี้ไปที่ checkout แทนตัวที่ติดตั้ง

ก่อนรันจากซอร์สให้จดค่าเดิมไว้ และคืนค่าหลังทดสอบ:

```powershell
$k = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
(Get-ItemProperty $k).AIUsageMonitor            # จดไว้ก่อน
Set-ItemProperty -Path $k -Name AIUsageMonitor `
  -Value '"C:\Program Files\AIUsageMonitor\AIUsageMonitor.exe"'   # คืนค่า
```

ในสคริปต์ทดสอบอัตโนมัติให้ stub `startup.set_enabled` เป็น no-op ก่อนสร้าง `MainWindow`

การขับ UI ด้วยการคลิกตามพิกัดหน้าจอเคยพลาดไปโดน combo box ของ header และเปลี่ยน
`metric` กับ `range_days` ในค่าตั้งจริงของผู้ใช้ ให้ใช้ UI Automation เรียกตามชื่อปุ่ม
หรือเรียกเมธอดของหน้าต่างตรง ๆ แทนการคลิกตามพิกัด

## บันทึกการแก้ไข 2026-09-15 — Windows: tray, widget, สัญญาอนุญาต และการปล่อยรุ่น

รอบนี้ทำบน Windows ทั้งหมด งานหลักคือสิ่งที่ผู้ใช้รายงานเข้ามาโดยตรง
และข้อบกพร่องที่เจอระหว่างตรวจของที่ build ออกมาจริง

### สิ่งที่เปลี่ยน

- **Codex quota** สำหรับ OpenAI พร้อม Codex-first detection (รายละเอียดอยู่หัวข้อด้านบน)
- **มิเตอร์เป็นไฟจราจร** เขียว / เหลืองที่ 75% / แดงที่ 90% บนรางสีเทากลาง ๆ
  รางเดิมเป็นสีน้ำเงินซึ่งกลายเป็นสีที่สี่ในสัญญาณสามสถานะ
  แดงกับเขียวเป็นคู่ที่คนตาบอดสีสับสนบ่อยที่สุด ทุกมิเตอร์จึงต้องมีทั้งสัญลักษณ์
  และคำกำกับเสมอ **ห้ามให้สีแบกความหมายลำพัง**
- **`gauge.py` ไม่เคยเรียก `severity_for()`** ใช้ค่า severity ที่เซิร์ฟเวอร์ส่งมาดิบ ๆ
  ขณะที่ widget กับ tray เรียกทั้งคู่ ไม่มีใครเห็นเพราะ provider ปัจจุบันคำนวณมาให้
  แต่บริการที่รายงาน "normal" ที่ 95% จะวาดเขียวบนหน้านี้และแดงที่อื่น แก้แล้ว
- **tray icon วาดเฉพาะ 5-hour window** เดิมหยิบ meter ตัวแรกที่มีเปอร์เซ็นต์
  ซึ่งบังเอิญถูกเพราะ provider เรียง session ไว้ก่อน
  ว่าอันไหนคือ 5 ชั่วโมงให้ถามจากข้อมูลที่บริการส่งมา ไม่ใช่เดาจากลำดับ
  **เมนูคลิกขวายังลิสต์ทุกช่วงเวลา ห้ามกรองให้เหลือ 5-hour**
- **tray ไม่หยุดหมุนเมื่อบริการล้มเหลว** เดิมบริการที่ refresh ไม่สำเร็จจะหลุดจาก
  `_order` ทั้งตัว เหลือบริการเดียวแล้ว rotation หยุด ดูไม่ต่างจากไอคอนค้าง
  ตอนนี้เก็บค่าล่าสุดที่ได้มาจริงไว้ แล้วบอกว่าเป็นค่าเก่า
- **widget ปรับขนาดได้** ขอบนอก 7 px ส่งให้ `startSystemResize` ของ Windows จัดการ
  ต่ำสุด 150×96 เปิดที่ 230×175 และหมุนเปลี่ยนบริการทุก 4 วินาที
  (ช้ากว่า tray ที่ 2 วินาทีโดยตั้งใจ เพราะ widget สลับทั้งแถวเกจ อ่านไม่ทัน)
- **สัญญาอนุญาต** ยึด GPL-3.0 เพราะ Qt for Python เสนอ GPL-3.0 เป็นตัวเลือกอยู่แล้ว
  งานที่แจกจ่ายจึงเป็น GPL-3.0 สม่ำเสมอ ไม่ต้องพึ่งเงื่อนไข relink ของ LGPL
  `THIRD-PARTY-NOTICES.md` ระบุทุกส่วนที่ถูกแจกจ่ายจริง อ่านจาก build tree ไม่ใช่จากความจำ
  ทั้ง `LICENSE` และ notices เดินทางไปกับไบนารีและอยู่ในโฟลเดอร์ติดตั้งด้วย

### กับดักที่เสียเวลาไปมาก อย่าให้ซ้ำ

- **Git for Windows ใน PATH ทำให้ไบนารีอ้วนขึ้น 2.5 MB** PyInstaller เก็บ
  `libcrypto-3-x64.dll` / `libssl-3-x64.dll` จาก `C:\Program Files\Git\mingw64\bin`
  ทั้งที่ Python ใช้ `C:\Python314\DLLs\libcrypto-3.dll` คนละตัว
  เลี่ยงโดย build ด้วย PATH ที่ตัดสองโฟลเดอร์ของ Git ออก
  ถ้าจะแก้ถาวรต้องกรองใน `.spec` **ตัดผิดตัวแล้ว HTTPS พังทั้งแอป ต้องเปิดของจริงตรวจ**
- **ตัวติดตั้งไม่ลบไฟล์ที่หายไประหว่างเวอร์ชัน** ติดตั้งทับแล้ว DLL เก่ายังค้างใน
  `_internal` ถ้าเปลี่ยนรายการไฟล์ที่แจก ให้ถอนก่อนติดตั้งเพื่อตรวจของจริง
- **ISCC ล้มเหลวแล้วสคริปต์เคยรายงานว่าสำเร็จ** เพราะเป็น native exe จึงไม่ทริกเกอร์
  `$ErrorActionPreference` แล้วส่วนสรุปไป glob หาไฟล์ใหม่สุดเจอของรุ่นก่อนมารายงาน
  ตอนนี้ `build_ai_installer.ps1` อ่านเวอร์ชันจาก `.iss` ลบไฟล์เป้าหมายก่อน
  แล้วตรวจทั้ง exit code และว่าไฟล์เกิดจริง
- **`git add -A` หลังลบ rule ใน `.gitignore`** กวาดไฟล์ที่เคยถูก ignore เข้ามา 2.6 MB
  โดยไม่มีใครรู้ **ดู `git diff --cached --stat` ก่อน commit ทุกครั้ง**
- **เทสต์ที่เรียก `_render()` ตรง ๆ พิสูจน์ไม่ได้ว่า timer ยิงจริง** ตอน tray ค้าง
  เทสต์เดิมผ่านหมดเพราะไม่เคยรอเวลาจริง ให้สุ่มตัวอย่างไอคอนตามเวลาแล้ว hash เทียบ

### การปล่อยรุ่น

ชื่อไฟล์ใช้รูปแบบเดียวกันทุก release โดยใช้ยัติภังค์ล้วน

```
AIUsageMonitor-Setup-<version>.exe       ตัวติดตั้ง (Inno Setup สร้างชื่อนี้เอง)
AIUsageMonitor-<version>-portable.exe    ตัวพกพา
AIUsageMonitor-<version>-arm64.dmg       macOS
```

ชื่อ release ใช้ `AI Usage Monitor <version>` เปลี่ยนชื่อ asset ทีหลังได้ผ่าน API
โดยไม่ต้องอัปโหลดใหม่ แต่ลิงก์ดาวน์โหลดเดิมจะใช้ไม่ได้

เมื่อเปลี่ยนเวอร์ชันต้องรักษา installer `AppId` เดิมไว้เสมอ
และ `tests/test_version.py` จะ fail ถ้า `version_info.txt` กับ `.iss` ไม่ตรงกับ `__init__.__version__`
