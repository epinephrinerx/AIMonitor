# การดึงข้อมูล Usage / Quota ของ Antigravity (AGY) ไปแสดงผลภายนอก

คุณสามารถดึงข้อมูลโควต้าและการใช้งาน (Usage/Quota) ออกมาในรูปแบบ **JSON** หรือ **Plain Text** ได้โดยตรงผ่าน CLI ของ Antigravity (`agy`) ด้วยโหมด Print (`-p` หรือ `--print`)

---

## 1. วิธีดึงข้อมูลผ่าน Command Line

### ดึงข้อมูลเป็น JSON (แนะนำสำหรับการนำไปประมวลผลต่อ)
```bash
agy -p "/usage" --output-format json
```

### ดึงข้อมูลเป็นข้อความสรุป (Plain Text)
```bash
agy -p "/usage"
```

> **หมายเหตุ:** คำสั่ง `/quota` ให้ผลลัพธ์โครงสร้างเดียวกันกับ `/usage`

---

## 2. โครงสร้างข้อมูล JSON ที่ได้
เมื่อรันคำสั่ง `agy -p "/usage" --output-format json` จะได้ข้อมูลโครงสร้างที่มีรายละเอียดครบถ้วน เช่น:

```json
{
  "conversation_id": "",
  "status": "SUCCESS",
  "response": "Gemini Models\tWeekly Limit Remaining\t88%\t2026-10-21T04:49:07Z\n...",
  "command": {
    "name": "usage",
    "data": {
      "description": "Within each group, models share a weekly limit and a 5-hour limit...",
      "groups": [
        {
          "name": "Gemini Models",
          "description": "Models within this group: Gemini Flash, Gemini Pro",
          "buckets": [
            {
              "id": "gemini-weekly",
              "name": "Weekly Limit Remaining",
              "description": "You have used some of your weekly limit, it will fully refresh in 5 days, 15 hours.",
              "window": "weekly",
              "remaining_fraction": 0.8819,
              "reset_time": "2026-10-21T04:49:07Z"
            },
            {
              "id": "gemini-5h",
              "name": "Five Hour Limit Remaining",
              "description": "You have used some of your 5-hour limit, it will fully refresh in 1 hour, 4 minutes.",
              "window": "5h",
              "remaining_fraction": 0.8881,
              "reset_time": "2026-10-15T14:00:27Z"
            }
          ]
        },
        {
          "name": "Claude and GPT models",
          "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
          "buckets": [
            {
              "id": "3p-weekly",
              "name": "Weekly Limit Remaining",
              "window": "weekly",
              "remaining_fraction": 1.0,
              "reset_time": "2026-10-22T12:55:46Z"
            },
            {
              "id": "3p-5h",
              "name": "Five Hour Limit Remaining",
              "window": "5h",
              "remaining_fraction": 1.0,
              "reset_time": "2026-10-15T17:55:46Z"
            }
          ]
        }
      ]
    }
  }
}
```

### ฟิลด์สำคัญที่นำไปประมวลผลต่อ
- `remaining_fraction`: โควต้าคงเหลือในรูปทศนิยม `0.0 - 1.0` (เช่น `0.88` = 88%)
- `reset_time`: วัน/เวลาที่จะรีเซ็ตโควต้ากลับเป็นเต็มจำนวน (มาตรฐาน ISO 8601 UTC)
- `window`: รอบเวลาการคำนวณโควต้า (`weekly` สำหรับรอบสัปดาห์ หรือ `5h` สำหรับรอบ 5 ชั่วโมง)
- `name`: ชื่อกลุ่มโมเดล (`Gemini Models`, `Claude and GPT models`)

---

## 3. ตัวอย่างโค้ดนำไปใช้งาน

### Python
```python
import subprocess
import json

def get_antigravity_usage():
    # เรียกคำสั่ง CLI ดึงข้อมูลเป็น JSON
    result = subprocess.run(
        ["agy", "-p", "/usage", "--output-format", "json"],
        capture_output=True,
        text=True,
        check=True
    )
    payload = json.loads(result.stdout)
    groups = payload.get("command", {}).get("data", {}).get("groups", [])

    usage_summary = {}
    for group in groups:
        group_name = group["name"]
        usage_summary[group_name] = {}
        for bucket in group.get("buckets", []):
            remaining_pct = round(bucket["remaining_fraction"] * 100, 1)
            usage_summary[group_name][bucket["name"]] = {
                "remaining": f"{remaining_pct}%",
                "resets_at": bucket["reset_time"]
            }
    return usage_summary

if __name__ == "__main__":
    print(json.dumps(get_antigravity_usage(), indent=2))
```

### PowerShell (Windows)
```powershell
$json = agy -p "/usage" --output-format json | ConvertFrom-Json
$groups = $json.command.data.groups

foreach ($group in $groups) {
    Write-Host "=== $($group.name) ===" -ForegroundColor Cyan
    foreach ($bucket in $group.buckets) {
        $pct = [math]::Round($bucket.remaining_fraction * 100, 1)
        Write-Host "$($bucket.name): $pct% (Reset: $($bucket.reset_time))"
    }
}
```

### Node.js (JavaScript / TypeScript)
```javascript
const { execSync } = require('child_process');

function getUsage() {
  const output = execSync('agy -p "/usage" --output-format json', { encoding: 'utf-8' });
  const data = JSON.parse(output);
  return data.command.data.groups;
}

console.log(JSON.stringify(getUsage(), null, 2));
```

---

## 4. แนวทางการนำไปประยุกต์ใช้งาน
- **Dashboard Web App**: ทำ Background worker เรียก cron job ทุก 15-30 นาที เพื่ออัปเดตสถิติลงฐานข้อมูลหรือแสดงผลบนเว็บ
- **Desktop Status Bar / Widget**: ใช้สคริปต์ PowerShell หรือ BitBar/Raycast/Rainmeter ดึงโควต้าขึ้นมาแสดงบน Desktop หรือ System Tray
- **Notification Alert**: แจ้งเตือนผ่าน Discord Webhook / LINE Notify เมื่อโควต้าใกล้หมด (เช่น `remaining_fraction < 0.10`)
