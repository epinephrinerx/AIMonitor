# Handoff — R-002 เฟส 5: Widget ปรับขนาดได้ + จำนวนเกจตามความกว้าง

Coding Owner → Writing Owner (Antigravity) · branch `agent/antigravity-widget-resize` · ฐาน `feat/csharp-rewrite` @ f8b4ac2
อ้างอิง 1.3.3 (อ่านอย่างเดียว): `ai_usage_monitor/widgets/compact.py` (`paintEvent`, `minimum_useful_height`, ค่าคงที่), `settings.py` (`WIDGET_MAX_EDGE=300`, `WIDGET_MIN_W=150`, `WIDGET_MIN_H=96`), `main_window.py` (~753–800, เปิด widget ที่ 230×175)

## ช่องว่างที่พิสูจน์จากโค้ด 2.0
- `WidgetWindow` ขนาดตายตัว 240×240, ปรับขนาดไม่ได้ (ไม่มี resize band) และแสดงเกจเดียวเสมอ (`Meters[0]`) — 1.3.3 แสดงหลายเกจตามความกว้าง
- ไม่จำขนาด widget (บันทึกแค่ตำแหน่งตอนปิด; restore ใช้เฉพาะ Left/Top)
- ไม่มีตรรกะลดรายละเอียดเมื่อเล็ก (subtitle → ย้ายเปอร์เซ็นต์ไปไว้ caption → "Too small to show the meters.")
- ไม่มีขั้นต่ำจาก font metrics

## รอบ 5a — ตรรกะล้วน + ViewModel + tests (ไม่แตะ XAML)
1. `WidgetLayout` (ใหม่, `src/AIMonitor.Presentation.Wpf/`, ไม่พึ่ง WPF type นอกจาก primitive): ค่าคงที่ตาม 1.3.3: `Margin=11`, `ArcMax=76`, `ArcMin=44`, `ArcFloor=28`, `Gap=6`, `InlineValueMin=46`, `MinWidth=150`, `MinHeight=96`, `MaxEdge=300`, `DefaultWidth=230`, `DefaultHeight=175`, `ResizeMargin=7`.
   - `static WidgetLayoutResult Compute(double width, double height, int meterCount, double headerHeight, double lineHeight, bool hasStatus)` → `{ int Count; double Arc; int CaptionLines; bool InlineValue; bool TooSmall; double Cell }` ทำตามลำดับของ `paintEvent` 1.3.3 อย่างตรงตัว:
     - `y = Margin + headerHeight + 5`; `contentWidth = width - 2*Margin`; `footer = hasStatus ? lineHeight + 4 : 0`
     - **จำนวนเกจตัดสินจากความกว้างเท่านั้น**: `count = meterCount; while (count>1 && (contentWidth - Gap*(count-1))/count < ArcMin) count--` (ห้ามเอาความสูงมาลด count เด็ดขาด)
     - `room = height - y - Margin - footer`; captionLines=2, `labelBlock = lineHeight*captionLines+3`, `arc = min(ArcMax, byWidth, room-labelBlock)`; ถ้า `arc<ArcMin && captionLines>1` → captionLines=1 แล้วคำนวณใหม่ (subtitle ถูกสละก่อน, title ไม่หาย)
     - ถ้า `arc<ArcFloor`: ถ้า `room < ArcFloor+labelBlock` → `TooSmall=true`; ไม่งั้น `arc=ArcFloor`
     - `InlineValue = arc >= InlineValueMin`; `Cell = (contentWidth - Gap*(Count-1))/Count`
   - `static double MinimumUsefulHeight(double headerHeight, double lineHeight)` = `Margin + headerHeight + 5 + ArcFloor + lineHeight + 3 + lineHeight + 4 + Margin + 1`
   - `static (double W,double H) Clamp(double w,double h, double minHeight)` → W ใน [MinWidth, MaxEdge], H ใน [min(MaxEdge,max(MinHeight,minHeight)), MaxEdge]
2. `WidgetViewModel`: เพิ่มที่คงสถานะ layout: `UpdateLayout(double width,double height,double headerHeight,double lineHeight)` คำนวณ `WidgetLayoutResult` จาก `CurrentProvider.Meters` (นับเฉพาะ meter ที่มีค่า) แล้วเปิด property: `VisibleMeters` (IReadOnlyList<MeterDisplayItem>, ตัดเหลือ Count), `ArcSize`, `ShowSubtitle` (CaptionLines==2), `InlineValue`, `IsTooSmall`; แจ้ง PropertyChanged; เรียกใหม่เมื่อ `CurrentProvider` เปลี่ยนด้วย (เก็บ width/height ล่าสุด)
3. tests (แดงเมื่อย้อน production): ตารางกรณีของ `Compute` — กว้างพอ 3 เกจ/แคบเหลือ 1; **สูงไม่พอไม่ลด count ไม่ว่ากว้างแค่ไหน** (ลากกว้างขึ้นแล้ว count กลับ); subtitle ถูกสละก่อน; arc<ArcFloor+room พอ → arc=ArcFloor; room ไม่พอ → TooSmall; InlineValue ที่ขอบ 46; hasStatus 17px ไม่ทำให้ count ตก; `MinimumUsefulHeight` ตรงสูตรและ `Clamp` ไม่ให้ min > max; VM: `UpdateLayout` ตัด VisibleMeters ตามความกว้าง, เปลี่ยน CurrentProvider แล้วคำนวณใหม่, meter ไม่มีค่าไม่ถูกนับ

## รอบ 5b — XAML + จำขนาด (ส่งแยกหลัง 5a ผ่าน)
`WidgetWindow.xaml`: `ResizeMode=CanResize` + `WindowChrome` (CaptionHeight=0, ResizeBorderThickness=7, GlassFrameThickness=0), MinWidth=150, MaxWidth/MaxHeight=300, ขนาดเริ่ม 230×175; แทน `GaugeControl` เดียวด้วย ItemsControl แถวเดียวผูก `VisibleMeters` (ขนาด arc = `ArcSize`, subtitle ซ่อนเมื่อ !ShowSubtitle, เปอร์เซ็นต์ย้ายไป caption เมื่อ !InlineValue), ข้อความ "Too small to show the meters." เมื่อ `IsTooSmall`; code-behind: `SizeChanged` → `vm.UpdateLayout` (วัด header/line height จาก `Typeface`/`FormattedText` ของฟอนต์จริง) และตั้ง `MinHeight` จาก `WidgetLayout.MinimumUsefulHeight`; restore ขนาดจาก placement ที่บันทึกไว้ผ่าน `WidgetLayout.Clamp`; บันทึกขนาดตอนปิดและเมื่อ resize จบ (ผ่าน `WindowPlacementRecorder`) tests STA: ลากขนาด → count เปลี่ยน, ขนาดที่จำถูก clamp, MinHeight ตาม metrics

## Constraints
ไม่ push/merge · trailer `Assisted-by: Antigravity (Google)` · ห้าม NuGet ใหม่ · ห้ามแตะ schema `AppSettings` · ห้าม fetch เครือข่ายจากการ resize · ถ้ากติกาขัดกับโค้ดจริงให้หยุดรายงาน
