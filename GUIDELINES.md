# แนวทางทำงานร่วมกันในโปรเจกต์ AI Usage Monitor

อ่าน [README.md](README.md) สำหรับพฤติกรรมโปรแกรม สถาปัตยกรรม และวิธี build
ไฟล์นี้บันทึกข้อกำหนดและบริบทสำหรับ Claude Code, Codex และผู้พัฒนาที่รับงานต่อ

นี่คือต้นฉบับเพียงไฟล์เดียว `CLAUDE.md` กับ `AGENTS.md` เป็นเพียงตัวชี้มาที่ไฟล์นี้
เพราะเครื่องมือแต่ละตัวมองหาชื่อของตัวเอง แก้ที่นี่ที่เดียวพอ

## โปรเจกต์ที่ต้องแก้

- แอปเดสก์ท็อป Windows **และ macOS** ใช้ Python + PySide6 มี Dashboard, Widget
  และ System tray (บน macOS คือเมนูบาร์)
- ซอร์สหลักคือ `ai_usage_monitor/`; entry point คือ `run_ai_monitor.py`
- `run_ai_monitor.py` เป็น entry point เดียว โปรแกรมรุ่นเก่า `claude_monitor/` กับ `run_app.py` ถูกลบออกจาก repo แล้ว ยังกู้ได้จาก history ถ้าจำเป็น
- Windows: ใช้ `.venv\Scripts\python.exe` และ `AIUsageMonitor.spec` สำหรับ portable build
- macOS: ใช้ `.venv/bin/python` และ `AIUsageMonitor-mac.spec`; `./build_mac.sh` ทำครบทั้ง .app และ .dmg
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

บน macOS:

```bash
QT_QPA_PLATFORM=offscreen .venv/bin/python -m unittest discover -s tests
.venv/bin/python -m ai_usage_monitor
./build_mac.sh
```

`QT_QPA_PLATFORM=offscreen` จำเป็นเฉพาะตอนรันเทสต์ ไฟล์เทสต์ตั้งเองอยู่แล้วแต่ตั้งซ้ำไม่เสียหาย
คำเตือน `Populating font family aliases ... "Sans Serif"` ระหว่างเทสต์เป็นของ platform
offscreen เอง ไม่ใช่ของโค้ดเรา (บน cocoa ฟอนต์ปริยายคือ `.AppleSystemUIFont`)

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

## หนี้ที่ค้างอยู่ ณ 2026-09-18 — จากการตรวจโค้ดและการยืนยันซ้ำ

รายงาน `CODE_REVIEW_2026-09-18.md` ถูกย่อยเข้ามาที่นี่แล้วและลบไฟล์ทิ้ง เพราะไฟล์นี้
ประกาศตัวเป็นต้นฉบับเดียว การมีรายงานลอยอยู่ข้าง ๆ คือจุดเริ่มของการ drift แบบเดียวกับ
ที่ `CLAUDE.md` กับ `AGENTS.md` เคยเจอ ทุกข้อด้านล่างไล่อ่านโค้ดยืนยันแล้วว่าเกิดขึ้นจริง
ไม่ได้คัดลอกข้อสรุปมาเฉย ๆ

### ยังไม่ได้แก้ เรียงตามลำดับที่ตั้งใจจะทำ

1. ~~**`ConnectDialog` Clear แล้ว Cancel ก็ยังลบ key**~~ — **แก้แล้ว 2026-09-18** ดูหัวข้อถัดไป
2. ~~**คำขอ refresh หายระหว่าง refresh**~~ **แก้แล้ว 2026-09-18** `MainWindow.refresh()` `return` ทันทีเมื่อ
   `_refreshing` เป็นจริง เปลี่ยน range หรือ metric ตอน worker ทำงานอยู่แล้วคำขอหายไปเลย
   ผลเก่าจึงถูก render ใต้ตัวเลือกใหม่จนกว่า timer รอบถัดไปจะมา ซึ่งนานได้ถึง 30 นาที
   ต้องเก็บคำขอล่าสุดเป็น pending แล้ว dispatch หลัง `_on_result()` พร้อม request id
   เพื่อทิ้งผลที่ล้าสมัยแทนที่จะวาด
3. ~~**ปิดโปรแกรมระหว่าง network request รอไม่พอ**~~ **แก้แล้ว 2026-09-18** `closeEvent()` รอ 2 วินาที แล้วรอเพิ่ม
   `api.TIMEOUT_SECONDS + 2` รวม **19 วินาที** แต่ worker เรียก provider เรียงกัน และ
   งบเวลาจริงคือ Claude 15 + Codex 25 + Gemini 20 = **60 วินาที** ในกรณีเลวร้าย
   เกินงบแล้ว `super().closeEvent()` เดินต่อขณะ QThread ยังทำงาน ซึ่งทำให้โปรเซส abort
   ต้องมี cancellation event ที่ provider กับ Codex client ตรวจระหว่างทาง และ deadline รวม
4. ~~**Gemini ใช้ UTC boundary แต่ UI ใช้วัน local**~~ **แก้แล้ว 2026-09-18** `day_start` มาจาก
   `now(utc).replace(hour=0)` ขณะที่ history อ่าน bucket ด้วย `when.astimezone().date()`
   เวลาไทย UTC midnight คือ 07:00 ทำให้ **Today ตกข้อมูล 00:00–06:59 ทุกวัน**
   ต้องหา local midnight ก่อนแล้วค่อยแปลงเป็น UTC ให้ Monitoring API
   และใช้ timezone เดียวกันทั้ง query interval, bucket parsing และ label
5. ~~**transcript ที่ข้อมูลผิดรูปลบเกจโควตาทั้งแท็บ**~~ **แก้แล้ว 2026-09-18** `TranscriptStore._ingest()` ใส่
   message id ลง `_seen_ids` **ก่อน** validate timestamp และก่อน `int(...)` ทุกตัว
   และจุดที่เรียก `_ingest()` ไม่มี try/except ครอบ ผลจริงเป็นสองจังหวะ:
   refresh แรก `ValueError` ลอยถึง `worker.py` แล้ว snapshot กลายเป็น `Unexpected error`
   **เกจโควตาหายไปด้วย** ซึ่งผิดข้อกำหนด "ห้ามทำโควตาหายเมื่อ history ล้มเหลว" ตรง ๆ
   refresh ถัดไป id ติด seen-set แล้วจึงข้าม record นั้นและกลับมาทำงานได้
   แต่ **นับ record นั้นไม่ครบตลอดไป** ต้อง validate ให้จบก่อนค่อย dedupe
   รับเฉพาะ int ที่ไม่ติดลบและไม่ใช่ bool และข้ามเฉพาะ record ที่เสีย
6. ~~**quota สำเร็จแต่ history ล้ม ถูกรายงานเป็น provider ล้มทั้งตัว**~~ **แก้แล้ว 2026-09-18** Codex เขียนลง
   `snapshot.error` ต้องแยกเป็น `history_error` เพื่อให้โควตาที่สดยังนับเป็น refresh สำเร็จ
   **การแตะ `ProviderSnapshot` คือการแตะสัญญากลางของทุก provider** ต้องไล่ให้ครบทั้ง
   dashboard, widget, tray และ report
7. ~~**สี severity บน dashboard ไม่ตรงกับ arc**~~ **แก้แล้ว 2026-09-18** `QuotaGauge.set_meter()` คำนวณ
   `severity_for()` ถูกต้องแล้วใช้กับ arc และคำกำกับ แต่ `_restyle_severity()` กลับไปอ่าน
   `self.meter.severity` ดิบ server ที่ส่ง `normal` ที่ 95% จึงได้ arc แดงกับคำว่า Critical
   แต่สีตัวอักษรยังเป็นสีปกติ **สีกับคำขัดกันเองผิดกฎ accessibility ของโปรเจกต์นี้**
8. ~~**version ยังหลุดสามจุด**~~ **แก้แล้ว 2026-09-18** `README.md` บอก installer 1.2.7, comment ใน
   `build_ai_installer.ps1` บอก 1.1.0 และ `codex_usage.py` ส่ง `clientInfo` เป็น 1.1.0
   ตัวหลังสำคัญสุดเพราะเป็นค่าที่ส่งออกนอกเครื่องจริง ให้ `import __version__`
   แล้วขยาย `tests/test_version.py` ให้คุมทั้งสามจุด
9. ~~**build script ตรวจแค่ว่า venv มีอยู่ ไม่ได้ตรวจว่ารันได้**~~ **แก้แล้ว 2026-09-18** ให้ลองรัน
   `& $python -c "import sys"` ก่อน แล้วแจ้งวิธีสร้างใหม่ถ้าล้ม

### ข้อที่รายงานบอกว่าเสีย แต่ตรวจแล้วไม่เสีย

รายงานระบุว่า `.venv` พังและมี 4 tests error ตรวจเมื่อ 2026-09-18 แล้วพบว่า
`.venv\Scripts\python.exe` รันได้ปกติ (3.14.3) และชุดทดสอบ **51 กรณีผ่านทั้งหมด**
อาการ error เกิดจากผู้ตรวจไปใช้ `C:\Python314\python.exe` ซึ่งไม่มี PySide6
จึง import `log_dialog.py` ไม่ได้ เป็นปัญหาของ environment ที่เลือกใช้ ไม่ใช่ของ repo
**ใช้ `.venv\Scripts\python.exe` เสมอตามที่หัวข้อทดสอบและ build กำหนดไว้**

### แก้แล้ว 2026-09-18 — ปุ่มในกล่องเชื่อมต่อทุกปุ่มทำตามที่มันบอก

`ConnectDialog._clear_key()` เคยเรียก `set_provider_key(id, "")` ทันทีที่กด ทั้งที่กล่อง
ยังมีปุ่ม Save กับ Cancel อยู่ อาการจริงหนักกว่าที่รายงานเขียนไว้หนึ่งชั้น เพราะ `_save()`
เขียนเฉพาะตอนช่องกรอกมีข้อความ ("blank = เก็บของเดิมไว้") ดังนั้น **กด Clear แล้วจะกด
Save หรือ Cancel ก็ลบทั้งคู่ และไม่มีทางเอากลับ** key ที่ผนึกด้วย DPAPI พิมพ์ใหม่จากความจำ
ไม่ได้

ตอนนี้ Clear แค่ตั้ง `_clear_requested` กับล้างช่องกรอกและเปลี่ยน placeholder เป็น
`(cleared when you save)`; `_save()` เท่านั้นที่เขียน settings โดยเรียงลำดับความตั้งใจว่า
พิมพ์ key ใหม่มา = แทนที่, ช่องว่างและกด Clear ไว้ = ลบ, ช่องว่างเฉย ๆ = ไม่แตะ

ลบ `ProviderSettingsDialog._clear()` ทิ้งด้วย เป็น dead code ตั้งแต่ย้าย credential ไป
หน้า Connections และมันลบ key ทันทีแบบเดียวกัน การทิ้งไว้คือการเก็บตัวอย่างของรูปแบบที่ผิด

`tests/test_connect_dialog.py` คุมไว้สองชั้น: ชั้นพฤติกรรม 6 กรณี และชั้นสัญญาที่ parse
ซอร์สด้วย AST ยืนยันว่า **มีแต่ `_save` เท่านั้นที่เรียก `set_provider_key` /
`set_provider_extra` ได้** ถ้าใครเผลอเขียนจากปุ่มอีก test นี้จะ fail แม้ test พฤติกรรมจะรอด
ตรวจแล้วว่าทั้งสองชั้น fail จริงเมื่อย้อนโค้ดกลับเป็นของเดิม

เทสต์ตั้ง `AI_USAGE_MONITOR_SETTINGS_SCOPE` เป็น scope ของตัวเองและล้างทิ้งเมื่อจบ
จึงไม่แตะ key จริงของผู้ใช้ และตั้ง `QT_QPA_PLATFORM=offscreen` เพื่อให้รันได้โดยไม่ต้องมีจอ

### แก้แล้ว 2026-09-18 — history ล้มต้องไม่ลากโควตาไปด้วย

สองอาการนี้คือเรื่องเดียวกัน เปอร์เซ็นต์โควตามาจากเซิร์ฟเวอร์ของผู้ให้บริการ
ส่วน transcript ในเครื่องกับ endpoint ยอดรายวันไม่เกี่ยวกับมันเลย การทำ history หาย
จึงต้องไม่ทำให้เกจหายตาม

**`TranscriptStore._ingest()`** เรียงใหม่ให้ validate ครบก่อนแล้วค่อยจำ id
เพิ่ม `usage_log.token_count()` เป็นตัวแปลงค่าเดียวที่ทุกฟิลด์ต้องผ่าน รับ
ค่าที่หายไปเป็น 0 รับสตริงตัวเลขเหมือนโค้ดเดิม รับ float ที่ลงตัวพอดี
แต่ **ปฏิเสธ bool** (Python บอกว่า `isinstance(True, int)` แต่ transcript ที่เขียน
`True` เป็นจำนวน token คือคนเขียนสับสน) และปฏิเสธค่าติดลบ
จุดเรียก `_ingest()` มี try/except ครอบไว้เป็นชั้นสุดท้าย พร้อมคอมเมนต์อธิบายว่า
ไม่ควรมีอะไรหลุดมาถึงตรงนั้น แต่ทางเลือกของการคิดผิดคือ exception วิ่งออกไปถึง worker
แล้วลบ snapshot ทั้งก้อน `TranscriptStore.malformed` นับ record ที่อ่านไม่ได้
โดยไม่ log เนื้อหา เพราะ transcript คือบทสนทนาของผู้ใช้

**`ProviderSnapshot.history_error`** เป็นช่องใหม่แยกจาก `error`
`snapshot.ok` ยังดูแค่ `error` ดังนั้น refresh ที่ได้โควตาสดยังนับเป็นสำเร็จ
ไล่แก้ครบทั้งสาม provider: Codex เคยยัด `history_error` ลง `snapshot.error`,
Claude รายงาน `last_error`/`malformed` ผ่าน `_history_note()`,
Gemini แยก try/except ของช่วง history ออกมาเพื่อไม่ให้ทับเกจ Today ที่อ่านมาแล้ว

หน้า dashboard มี `history_note` ของตัวเองวางไว้ตรงที่กราฟจะอยู่ ไม่ใช่ banner ด้านบน
เพราะการขึ้นว่า "บริการนี้ล้มเหลว" คร่อมเกจที่กำลังทำงานอยู่คือคำที่ไม่จริง
ส่วน Usage log เขียนหมายเหตุลงไปด้วยแม้จะมีแถวข้อมูลมาแล้ว เพราะยอดที่ขาดไปบางส่วน
แต่ดูเหมือนครบคือสิ่งที่คนเอาไปอ้างต่อ

เทสต์: `tests/test_transcript_ingest.py` 20 กรณี และ `tests/test_partial_success.py`
12 กรณี ตรวจแล้วว่า 10 จาก 20 กรณีแรกแดงจริงเมื่อย้อนโค้ดกลับ
`test_openai_usage.test_history_failure_preserves_quota` เดิมเขียน assert ไว้ผิดช่อง
(ยืนยัน `snapshot.error` ซึ่งคือพฤติกรรมที่เป็นบั๊ก) แก้ให้ยืนยัน `history_error`
เป็น None ที่ `error` และ `ok` เป็นจริง พร้อมเพิ่มกรณีคู่ตรงข้ามว่าไม่มี quota window
ต้องยังนับเป็นล้มเหลวจริง

### แก้แล้ว 2026-09-18 — วงจร refresh และการปิดโปรแกรม

**คำขอที่มาระหว่าง refresh ไม่หายอีกแล้ว** `refresh()` เก็บคำขอล่าสุดไว้ที่
`_pending_refresh` แล้ว `_on_result()` ส่งต่อทันที เก็บแบบ **แทนที่ ไม่ใช่ต่อคิว**
เพราะเปลี่ยนช่วงวันสองครั้งติดกันต้องดึงช่วงที่สอง ไม่ใช่ดึงทั้งสองเรียงกัน

`RefreshResult` พก `days`, `metric` และ `request_id` กลับมาด้วย
**อะไรที่บรรยายผลลัพธ์ต้องอ่านจากตรงนี้ ห้ามอ่านจาก combo box** เพราะผู้ใช้อาจเปลี่ยน
ค่าไปแล้วระหว่างที่คำขอยังบินอยู่ `_build_report()` เปลี่ยนมาใช้ค่าจาก result แล้ว
ไม่งั้น log จะขึ้นหัวว่า 30 วัน/ดอลลาร์ คร่อมแถวที่เป็น 14 วัน/token
ส่วนกราฟไม่มีปัญหานี้อยู่แล้วเพราะมันเขียนหัวข้อจาก `history.days` ของตัวเอง

**ปิดโปรแกรมไม่ค้างและไม่ abort** งบเวลาเดิมคือ 2 วิ แล้วรอเพิ่ม
`api.TIMEOUT_SECONDS + 2` รวม 19 วินาที ซึ่งไม่เคยเป็นงบที่ถูก เพราะ provider
ถูกเรียกเรียงกัน กรณีเลวร้ายจริงคือผลรวม ~60 วินาที พอเกินงบแล้วหน้าต่างถูกทำลายต่อ
พา QThread ลูกไปด้วย และการลบ QThread ที่ยังทำงานทำให้โปรเซส abort

ตอนนี้เป็น cancellation แทนการรอ: `RefreshWorker.cancel()` ตั้ง `threading.Event`
ที่ worker เช็คก่อนเริ่ม provider ตัวถัดไป และส่งต่อให้ provider ผ่าน `Provider.cancel`
(เป็น attribute ไม่ใช่พารามิเตอร์ของ `fetch()` โดยตั้งใจ เพื่อให้สัญญาที่ทุก provider
ต้องทำยังเป็นคำถามเดียวเหมือนเดิม provider ที่ใช้ประโยชน์ได้เท่านั้นที่ต้องรู้ว่ามีอยู่)
`codex_usage._Client.call()` รอทีละ `CANCEL_POLL_SECONDS` แทนการ block ยาว 25 วินาที
และ `GeminiProvider._query()` เช็คก่อนยิงทุก request ซึ่งเป็นคอขวดเดียวของ Monitoring

`_release_worker()` สั่ง cancel รอ `SHUTDOWN_GRACE_MS` (3 วินาที) ถ้ายังไม่จบ
**ไม่ทำลาย thread** แต่ `setParent(None)` แล้วฝากไว้ที่ `_ABANDONED_THREADS`
ระดับโมดูล ให้ object อยู่รอดพ้นการ teardown ของหน้าต่าง แล้วปล่อยให้ process exit
เก็บกวาด งบ 3 วินาทีนี้เป็นเผื่อ socket read ที่ค้างอยู่เท่านั้น ไม่ใช่เผื่อทั้ง refresh
`tests/test_refresh_lifecycle.py` มี test ที่ยืนยันว่า `SHUTDOWN_GRACE_MS` ต้อง
**สั้นกว่า** ผลรวม timeout ของ provider เพื่อกันไม่ให้ใครแก้กลับไปเป็นการรออีก

เทสต์ 17 กรณี รวมถึงการสร้าง `MainWindow` จริงโดย stub `startup.set_enabled`,
`startup.reconcile`, tray และ worker thread ยืนยันแล้วว่า 2 กรณีแดงเมื่อย้อน queue ออก

**scope ของ settings ในเทสต์ต้องตั้งใน `setUp` ไม่ใช่ระดับโมดูล** เพราะ `Settings`
อ่าน env ใหม่ทุกครั้งที่สร้าง การตั้งตอน import ทำให้ไฟล์ที่ import ทีหลังคุมทุกไฟล์
ส่วน `QT_QPA_PLATFORM` ตั้งระดับโมดูลได้ เพราะ Qt อ่านครั้งเดียวตอนสร้าง QApplication

### แก้แล้ว 2026-09-18 — วันของผู้ใช้ และสีที่ตรงกับคำ

**Gemini ใช้วันตามเวลาท้องถิ่นแล้ว** เพิ่ม `local_day_start(day)` หาเที่ยงคืนท้องถิ่น
แล้วแปลงเป็น UTC ให้ Monitoring API สร้างจาก datetime แบบ naive โดยตั้งใจ
เพื่อให้ระบบใช้ offset ที่มีผลจริง ณ เวลานั้น การเรียก `.replace(hour=0)` บนค่า aware
จะลาก offset ของวันนี้ย้อนกลับไป ซึ่งผิดไปหนึ่งชั่วโมงในวันที่มีการเปลี่ยน DST
(ไทยไม่มี DST แต่ US Pacific มี และมี test ครอบทั้งสองแบบ)

เจอบั๊กซ้อนอีกชั้นระหว่างแก้: bucket ถูกตั้งชื่อวันจาก `interval.endTime`
ซึ่งเป็น**จุดจบ**ของช่วง 24 ชั่วโมง คือเที่ยงคืนของวันถัดไป ทุก bucket จึงเลื่อนไปหนึ่งวัน
แม้ไม่นับเรื่อง timezone เลย `bucket_day()` ใช้ `startTime` เป็นหลัก
และถอย `endTime` กลับมาหนึ่ง period เมื่อ payload มีแต่ endTime

**`QuotaGauge.effective_severity()`** เป็นแหล่งเดียวของ severity ที่หน้านี้แสดง
ทั้ง arc คำกำกับ และสี เดิม `set_meter()` คำนวณ `severity_for()` ให้ arc กับคำ
แต่ `_restyle_severity()` อ่าน `meter.severity` ดิบสำหรับสี บริการที่รายงาน `normal`
ที่ 95% จึงได้ arc แดง คำว่า Critical แต่สีตัวอักษรเป็นสีปกติ
**สีที่ขัดกับคำแย่กว่าอย่างใดอย่างหนึ่งผิดเดี่ยว ๆ** เพราะกฎ accessibility ของโปรเจกต์นี้
คือทั้งสองต้องยืนยันกันเอง meter ที่ไม่มีเปอร์เซ็นต์ (Gemini) คืนค่าที่เซิร์ฟเวอร์บอก
เพราะไม่มี threshold ท้องถิ่นให้ใช้

เทสต์ `tests/test_days_and_severity.py` 17 กรณี ยืนยันแล้วว่า 3 กรณีแดงเมื่อย้อนโค้ดกลับ

**บทเรียนจากการเขียน test timezone** ตัวช่วยจำลองโซนเวลาต้องครอบ `now()` ให้คืน
instance ของคลาสที่ patch ไว้ ไม่ใช่ `datetime` จริง เพราะโค้ดมักต่อ `.astimezone()`
ท้าย `now()` ทันที ถ้าคืนค่าเป็น datetime จริงมันจะไปอ่านโซนของเครื่องที่รันเทสต์
และต้องตีความค่า naive ว่าอยู่ในโซนที่จำลอง ไม่ใช่โซนของเครื่อง
ผมเขียนผิดทั้งสองจุดตอนแรกและทำให้ test แดงโดยที่โค้ดโปรแกรมถูกอยู่แล้ว

### แก้แล้ว 2026-09-18 — version, build script และการออกรุ่น 1.3.1

**version มีแหล่งเดียวจริง ๆ แล้ว** `codex_usage.py` import `__version__` แทนการเขียน
สตริงไว้ตรง ๆ ตัวนี้สำคัญที่สุดในสามจุดเพราะเป็น **ค่าเดียวที่ออกไปนอกเครื่อง**
มันบอกว่า 1.1.0 ขณะที่แอปเป็น 1.3.0 ซึ่งทำให้ log ฝั่ง Codex ระบุ build ต้นทางไม่ได้

`tests/test_version.py` ขยายเป็นคุมห้าจุดแล้ว: `__init__`, `version_info.txt`, `.iss`,
`README.md` และ header ของ `build_ai_installer.ps1` สองตัวหลังตรวจด้วย regex
ที่จับรูปแบบ `AIUsageMonitor-Setup-<version>` ส่วน clientInfo ตรวจด้วย AST
ว่าต้องเป็น `ast.Name` ไม่ใช่ literal เพราะการเทียบไฟล์จับ literal ไม่ได้
กฎคือ **ห้ามมี literal** test นี้จับ README ที่ยังบอก 1.2.7 ได้ทันทีที่เขียนเสร็จ

**build script ตรวจว่า venv รันได้ ไม่ใช่แค่มีอยู่** `Test-VenvUsable` ลองรัน
`python -c "import sys"` จริง เพราะ venv จำ path ของ interpreter ที่สร้างมัน
การอัปเกรดหรือถอน Python นั้นทิ้งไว้ซึ่ง `.venv` ที่มีอยู่แต่ตายแล้ว
และความล้มเหลวจะไปโผล่อีกหลายนาทีถัดมาข้างใน PyInstaller โดยไม่บอกสาเหตุ
ข้อความ error บอกวิธีแก้ให้เลย พร้อมอ่าน `home =` จาก `pyvenv.cfg` มาแสดง

**build script ตั้งชื่อ asset ให้ถูกตั้งแต่ต้น** คัดลอก portable ไปเป็น
`installer_out\AIUsageMonitor-<version>-portable.exe` วางคู่กับตัวติดตั้ง
เพราะรุ่น 1.3.0 เคยอัปโหลดเป็น `AIUsageMonitor.exe` เปล่า ๆ แล้วต้องตามไปเปลี่ยนชื่อ
ผ่าน API ทีหลัง ซึ่งทำให้ลิงก์ดาวน์โหลดเดิมใช้ไม่ได้

README แก้สามจุดที่ค้าง: บรรทัดติดตั้งบอก 1.2.7, ขนาด portable บอก 49 MB
(จริง 46.7 MB) และหัวข้อ "Working with Claude Code" ชี้ไป `CLAUDE.md`
ซึ่งตอนนี้เป็นแค่ pointer เปลี่ยนไปชี้ `GUIDELINES.md` โดยตรง

ผลตรวจรุ่น 1.3.1:

- ชุดทดสอบ 127 กรณีผ่าน (จาก 51 ตอนเริ่มวัน)
- เปิด **ไฟล์ .exe ที่ build จริง** `installer_out\AIUsageMonitor-1.3.1-portable.exe`
  ยืนยัน FileVersion 1.3.1.0 เมนูบาร์ ปุ่ม Widget/Refresh บน header และข้อมูลสดขึ้นครบ
- คืนค่า `Run` registry หลังเปิด build ทุกครั้งตามหัวข้อ "ข้อควรระวังตอนรันจากซอร์ส"
  ซึ่งใช้กับ .exe ที่อยู่นอกโฟลเดอร์ติดตั้งเหมือนกัน

## บันทึกการแก้ไข 2026-09-19 — พอร์ต macOS และ .dmg

รอบนี้ทำบน macOS 26.3 (Apple Silicon) ทั้งหมด งานคือทำให้แอปทำงานจริงบน Mac
ไม่ใช่แค่แพ็กใหม่ สาขา `Mac` แยกไปตั้งแต่ v1.1.0 แล้วรอบนี้ merge `main` ที่ v1.3.1
กลับเข้ามา **ผู้ใช้สั่งให้ทำงานบนสาขา `Mac` ต่อไป `main` ที่ตามหลังอยู่เป็นความตั้งใจ**

### ห้าจุดที่ผูกกับ Windows และต้องแก้จริง ไม่ใช่ข้ามไป

- **API key เก็บไม่ได้เลย** `secrets.py` มีแต่ DPAPI พอเป็น Mac `available()` เป็นเท็จ
  และกล่อง Settings ขึ้นว่าบันทึก key ไม่ได้ ทุก provider ที่ใช้ API key จึงตายหมด
  ตอนนี้ `seal`/`unseal` รับ `handle` เพิ่ม และมี backend Keychain หลัง API เดิม
  ค่าที่เขียนลง settings คือ `keychain:v1:<handle>` **ไม่ใช่ตัว key**
- **ล็อกอิน Claude Code อยู่ใน Keychain ไม่ใช่ไฟล์** บน Mac ไม่มี
  `~/.claude/.credentials.json` ถ้าไม่แก้ provider หลักจะรายงานว่ายังไม่ล็อกอินทุกเครื่อง
  ยืนยันกับเครื่องจริงแล้วว่าเป็นแบบนี้
- **`system_prefers_dark()` คืนเท็จเสมอนอก Windows** ธีม "ตามระบบ" จึงค้างที่สว่าง
  เปลี่ยนไปถาม `QStyleHints.colorScheme()` ของ Qt ซึ่งตอบได้ทั้งสองระบบ
- **Segoe UI ไม่มีบน macOS** Qt ไม่ฟ้องแต่แทนที่ให้เองทีละข้อความ หน้าต่างเดียวจึงมี
  ฟอนต์ปนกัน ดู `fonts.py`
- **เปิดพร้อมเครื่อง** Run key ใช้ไม่ได้ เปลี่ยนเป็น LaunchAgent

### ข้อกำหนดที่ตั้งใจไว้ อย่าเปลี่ยนโดยไม่ตั้งใจ

- **ห้ามส่ง API key ผ่าน argv** `security add-generic-password -w <key>` ทิ้ง key ไว้ใน
  command line ให้โปรเซสอื่นของผู้ใช้เดียวกันอ่านจาก `ps` ได้ ใช้ `security -i`
  ซึ่งรับคำสั่งทาง stdin เท่านั้น ทดลองยืนยันแล้วว่า `-w` ไม่เคยอ่าน stdin
  (มันกลืน argument ตัวถัดไปเป็นค่า password)
- **`secrets.forget()` ต้องถูกเรียกตอนล้าง key** บน Windows ลบค่าใน settings แล้ว
  ciphertext หายตาม แต่บน Mac ค่าใน settings เป็นแค่ตัวชี้ ถ้าไม่ลบ item ใน Keychain
  ด้วย การกด Clear จะไม่ได้ลบอะไรจริง
- **ชื่อ service ใน Keychain ต้องมี settings scope ต่อท้าย** ไม่งั้นเทสต์ที่ตั้ง
  `AI_USAGE_MONITOR_SETTINGS_SCOPE` จะไปทับ key จริงของผู้ใช้
- **detection ห้ามอ่าน "ข้อมูล" ใน Keychain** `MainWindow.redetect()` รันบน GUI thread
  การอ่านค่า secret จะเด้งกล่องขออนุญาตของ Keychain และค้างหน้าต่างไว้ข้างหลัง
  `credentials.probe()` จึงถามแค่ metadata (`find-generic-password` ไม่ใส่ `-w`)
  ซึ่งไม่ทริกเกอร์ ACL ส่วนตัว token อ่านใน `load()` บน worker thread
- **ห้าม `launchctl bootout` ตอนปิดสวิตช์ "เปิดพร้อมเครื่อง"** ถ้าโปรเซสที่กำลังรัน
  ถูก launchd เปิดมา การ bootout จะฆ่าแอปที่ผู้ใช้กำลังใช้อยู่ ลบไฟล์ plist พอ
  แล้วมีผลตั้งแต่ login ครั้งถัดไป
- **LaunchAgent คือรายการเดียวกับที่ System Settings → Login Items แสดง**
  กติกา "ระบบชนะหลัง first run" เหมือนฝั่ง Windows `is_enabled()` จึงเช็ค
  `launchctl print-disabled` ด้วย เพราะ Login Items เขียน override ทับ ไม่ได้ลบไฟล์เรา
- **`.app` ต้องพก LICENSE และ THIRD-PARTY-NOTICES.md** เหมือน .exe ข้อผูกพัน GPL-3.0
  ไม่ได้ขึ้นกับแพลตฟอร์ม
- **build เป็น one-directory + BUNDLE เท่านั้น ห้าม onefile** onefile บน macOS แตกไฟล์
  ทั้งก้อนลง temp ทุกครั้งที่เปิด ซึ่งเป็นราคาที่แอปเปิดพร้อม login ต้องจ่ายทุกเช้า

### กับดักที่เจอ อย่าให้ซ้ำ

- **`QFontDatabase.systemFont()` ตอบ `"Sans Serif"` บน platform offscreen**
  ซึ่งเป็นชื่อที่ไม่มีจริง Qt จึงต้องไล่สแกน alias ~130 ms แล้ว log เตือน
  `fonts.ui_font()` จึงใช้ `QFont()` เปล่า ๆ นอก Windows เพราะมันพกฟอนต์ของ
  application ที่ Qt ตั้งเป็นฟอนต์ระบบไว้แล้ว **ไม่ต้องเอ่ยชื่อฟอนต์เลย**
- **`css_family()` แยกตามแพลตฟอร์ม** ใส่ `Segoe UI` นำหน้าใน CSS ถูกบน Windows
  แต่เป็นตัวจุดชนวนการสแกนบน macOS พอดี
- **auto-merge ผ่านแต่โค้ดพัง** เมนู `Start on start up` ที่ main เพิ่มมาเรียก
  `settings.start_with_windows` ซึ่งสาขานี้เปลี่ยนชื่อเป็น `start_at_login` ไปแล้ว
  git ไม่เห็นเป็น conflict เพราะคนละบรรทัด และ**ไม่มีเทสต์ตัวไหนจับได้**
  หลัง merge ทุกครั้งให้ `grep` ชื่อเก่าที่สาขานี้เปลี่ยน ไม่ใช่เชื่อว่า merge สะอาด
- **การทดสอบเปิดแอปจะตั้ง login item ให้เอง** เพราะ `start_at_login` ปริยายเป็นจริง
  และ `reconcile()` รันใน `MainWindow.__init__` เหมือนฝั่ง Windows ที่เขียนเตือนไว้แล้ว
  ก่อนทดสอบให้ seed settings ใน scope ของตัวเอง:
  `defaults write com.aiusagemonitor.<scope> once -dict startupApplied -bool true`
  และ `startWithWindows -bool false` แล้วลบทิ้งเมื่อจบ
- **`walk_packages()` บนแพ็กเกจนี้จะเปิดแอปขึ้นมา** เพราะ `__main__.py` เรียก `main()`
  ตอน import ถ้าจะไล่ import ทุกโมดูลเพื่อตรวจ ต้องข้าม `__main__`
- **`screencapture` ใช้ไม่ได้จาก terminal ที่ไม่มีสิทธิ์ Screen Recording**
  ตอบว่า `could not create image from display` การตรวจหน้าตา UI จึงต้องให้คนเปิดดูเอง

### ทดสอบและ build ที่ทำจริงในรอบนี้

- ชุดทดสอบ **127 กรณีผ่านทั้งหมดบน macOS** ด้วย `QT_QPA_PLATFORM=offscreen`
  โดยไม่ต้องแก้เทสต์เดิมให้ข้าม มีแก้สองจุดในเทสต์เอง: `test_connect_dialog`
  ล้าง key ผ่าน `set_provider_key` เพื่อไม่ทิ้ง item ค้างใน Keychain
  และข้อความ skip ไม่พูดว่าเป็น Windows อย่างเดียวอีก
- `test_version.py` คุม `build_mac.sh` กับ `AIUsageMonitor-mac.spec` เพิ่ม
  ทั้งสองไฟล์อ่าน `__version__` เอง ไม่มี literal และเป็นตัวที่จับ README
  ที่ยังบอก 1.1.0 ได้ทันทีหลัง merge
- เปิด **ไฟล์ .app ที่ build จริง** `dist/AI Usage Monitor.app` ยืนยัน
  `CFBundleShortVersionString` เป็น 1.3.1, `codesign --verify --deep --strict` ผ่าน,
  เปิดค้างได้ไม่ crash, เปิดซ้ำแล้วตัวที่สองออกด้วยโค้ด 0 โดยตัวแรกยังอยู่
  และ LICENSE/THIRD-PARTY-NOTICES อยู่ในบันเดิลจริง
- **ยังไม่ได้ตรวจด้วยตา** สิทธิ์ Screen Recording ไม่มี จึงยังไม่ยืนยันหน้าตาฟอนต์
  โหมดมืด และเมนูบาร์บนจอจริง

### สิ่งที่ยังไม่ได้ทำบน macOS

- **ไม่ได้ notarize** เซ็นแบบ ad-hoc พอสำหรับเครื่องที่ build เอง แต่เครื่องอื่น
  ที่โหลดไฟล์ไปจะติด quarantine ต้องคลิกขวา → Open ครั้งแรก
  การแก้ถาวรต้องมี Developer ID Application แล้ว `notarytool submit` + `stapler staple`
- **arm64 อย่างเดียว** ถ้าต้องรองรับ Intel ด้วย ต้องใช้ Python แบบ universal2
  (ตัวติดตั้งจาก python.org เป็น universal2) แล้วตั้ง `target_arch="universal2"`
- **`codex_usage.py` ยังไม่ได้ทดสอบบน Mac** โค้ดจัดการ `codex` กับ `CREATE_NO_WINDOW`
  แยกตาม `os.name` ไว้ถูกแล้ว แต่ยังไม่มีใครรันคู่กับ Codex CLI จริงบนเครื่อง Mac
- **ยังไม่มี Intel/Windows CI** ทุกอย่างตรวจด้วยมือบนเครื่องเดียว
