"""Generates samples/sample-iec-2page.pdf — a small vector (CAD-style) IEC schematic used as a
test fixture for the recognizer: frame + title block, power page with breaker/contactor/motor,
control page with coil + contacts, junction dots, wire numbers, cross-refs and
origin/destination arrows between pages. All geometry is vector; no raster content."""
import sys
from reportlab.lib.pagesizes import A3, landscape
from reportlab.lib.units import mm
from reportlab.pdfgen import canvas

W, H = landscape(A3)
out = sys.argv[1] if len(sys.argv) > 1 else "sample-iec-2page.pdf"
c = canvas.Canvas(out, pagesize=(W, H))


def frame(page_no, title):
    c.setLineWidth(0.7)
    c.rect(10 * mm, 10 * mm, W - 20 * mm, H - 20 * mm)
    # column markers 1..8 along the top
    colw = (W - 20 * mm) / 8
    c.setFont("Helvetica", 7)
    for i in range(8):
        x = 10 * mm + i * colw
        c.line(x, H - 10 * mm, x, H - 15 * mm)
        c.drawCentredString(x + colw / 2, H - 14 * mm, str(i + 1))
    c.line(10 * mm, H - 15 * mm, W - 10 * mm, H - 15 * mm)
    # title block bottom-right
    tx, ty, tw, th = W - 190 * mm, 10 * mm, 180 * mm, 30 * mm
    c.rect(tx, ty, tw, th)
    c.line(tx, ty + 15 * mm, tx + tw, ty + 15 * mm)
    c.line(tx + 120 * mm, ty, tx + 120 * mm, ty + th)
    c.setFont("Helvetica", 7)
    c.drawString(tx + 2 * mm, ty + 25 * mm, "TITLE:")
    c.drawString(tx + 14 * mm, ty + 25 * mm, title)
    c.drawString(tx + 2 * mm, ty + 10 * mm, "DWG NO:")
    c.drawString(tx + 16 * mm, ty + 10 * mm, "TEW-1001")
    c.drawString(tx + 2 * mm, ty + 4 * mm, "REV:")
    c.drawString(tx + 12 * mm, ty + 4 * mm, "B")
    c.drawString(tx + 122 * mm, ty + 25 * mm, "SHEET:")
    c.drawString(tx + 135 * mm, ty + 25 * mm, f"{page_no} / 2")
    c.drawString(tx + 122 * mm, ty + 10 * mm, "PROJECT:")
    c.drawString(tx + 138 * mm, ty + 10 * mm, "Conveyor Line 3")


def wire(x1, y1, x2, y2):
    c.setLineWidth(0.35)
    c.line(x1 * mm, y1 * mm, x2 * mm, y2 * mm)


def dot(x, y):
    c.circle(x * mm, y * mm, 0.6 * mm, stroke=0, fill=1)


def text(x, y, s, size=7, rot=0):
    c.setFont("Helvetica", size)
    if rot:
        c.saveState(); c.translate(x * mm, y * mm); c.rotate(rot); c.drawString(0, 0, s); c.restoreState()
    else:
        c.drawString(x * mm, y * mm, s)


def breaker(x, y):  # vertical, pins at (x,y) bottom and (x,y+12) top
    c.setLineWidth(0.35)
    c.line(x * mm, y * mm, x * mm, (y + 4) * mm)
    c.line(x * mm, (y + 4) * mm, (x - 3) * mm, (y + 9) * mm)
    c.line(x * mm, (y + 9) * mm, x * mm, (y + 12) * mm)
    c.line((x - 1) * mm, (y + 8) * mm, (x + 1) * mm, (y + 10) * mm)
    c.line((x - 1) * mm, (y + 10) * mm, (x + 1) * mm, (y + 8) * mm)


def no_contact(x, y):  # IEC NO contact, vertical, pins y and y+12
    c.setLineWidth(0.35)
    c.line(x * mm, y * mm, x * mm, (y + 4) * mm)
    c.line(x * mm, (y + 4) * mm, (x - 3) * mm, (y + 9) * mm)
    c.line(x * mm, (y + 8.5) * mm, x * mm, (y + 12) * mm)


def nc_contact(x, y):
    c.setLineWidth(0.35)
    c.line(x * mm, y * mm, x * mm, (y + 4) * mm)
    c.line(x * mm, (y + 4) * mm, (x + 3) * mm, (y + 9) * mm)
    c.line(x * mm, (y + 8.5) * mm, x * mm, (y + 12) * mm)
    c.line(x * mm, (y + 8.5) * mm, (x + 3.5) * mm, (y + 8.5) * mm)


def coil(x, y):  # rectangle body 8x5, pins at y and y+12
    c.setLineWidth(0.35)
    c.line(x * mm, y * mm, x * mm, (y + 3.5) * mm)
    c.rect((x - 4) * mm, (y + 3.5) * mm, 8 * mm, 5 * mm)
    c.line(x * mm, (y + 8.5) * mm, x * mm, (y + 12) * mm)


def pushbutton_no(x, y):
    no_contact(x, y)
    c.line((x - 1.5) * mm, (y + 6.5) * mm, (x - 6) * mm, (y + 6.5) * mm)
    c.line((x - 6) * mm, (y + 5.5) * mm, (x - 6) * mm, (y + 7.5) * mm)


def motor(x, y):  # circle r=6, pin at top (x, y+6)
    c.setLineWidth(0.35)
    c.circle(x * mm, y * mm, 6 * mm)
    c.setFont("Helvetica", 9)
    c.drawCentredString(x * mm, (y - 1.2) * mm, "M")
    c.setFont("Helvetica", 6)
    c.drawCentredString(x * mm, (y - 3.5) * mm, "3~")


def lamp(x, y):  # circle with X, pins y and y+12
    c.setLineWidth(0.35)
    c.line(x * mm, y * mm, x * mm, (y + 3) * mm)
    c.circle(x * mm, (y + 6) * mm, 3 * mm)
    d = 2.1
    c.line((x - d) * mm, (y + 6 - d) * mm, (x + d) * mm, (y + 6 + d) * mm)
    c.line((x - d) * mm, (y + 6 + d) * mm, (x + d) * mm, (y + 6 - d) * mm)
    c.line(x * mm, (y + 9) * mm, x * mm, (y + 12) * mm)


def arrow_out(x, y):  # origin/destination arrow pointing right, pin at (x,y)
    c.setLineWidth(0.35)
    p = c.beginPath(); p.moveTo(x * mm, (y + 1.5) * mm); p.lineTo((x + 3) * mm, y * mm)
    p.lineTo(x * mm, (y - 1.5) * mm); p.close(); c.drawPath(p, stroke=1, fill=0)


# ---------------- Page 1: power ----------------
frame(1, "Power distribution")
xs = [60, 75, 90]
for i, x in enumerate(xs):
    wire(x, 250, x, 220)                 # incoming
    breaker(x, 208)                      # -Q1 pins 208..220
    wire(x, 208, x, 190)
    no_contact(x, 178)                   # -K1 main contacts 178..190
    wire(x, 178, x, 160)
    text(x - 2, 252, ["L1", "L2", "L3"][i])
text(96, 213, "-Q1"); text(96, 183, "-K1"); text(96, 179, "/2.3", 6)
# bus to motor
wire(60, 160, 90, 160); dot(75, 160)
wire(75, 160, 75, 136)
motor(75, 130)
text(84, 128, "-M1")
# control supply tap with junction dot -> arrow to page 2
dot(60, 200)
wire(60, 200, 140, 200)
arrow_out(140, 200)
text(145, 199, "/2.1")
text(110, 201.5, "101", 6)
wire(90, 196, 140, 196)
dot(90, 196)
arrow_out(140, 196)
text(145, 195, "/2.1")
text(110, 197.5, "102", 6)
c.showPage()

# ---------------- Page 2: control ----------------
frame(2, "Motor control")
# incoming origin arrows on the left
wire(40, 200, 60, 200); text(30, 199, "/1.3"); text(48, 201.5, "101", 6)
wire(40, 120, 60, 120); text(30, 119, "/1.3"); text(48, 121.5, "102", 6)
# rung 1: -S1 start PB, -K1 coil
wire(60, 200, 60, 180)
pushbutton_no(60, 168); text(66, 173, "-S1")
wire(60, 168, 60, 150)
coil(60, 138); text(66, 141, "-K1")
text(56, 132, "1.4", 6)
wire(60, 138, 60, 120)
# rung 2: -K1 seal-in NO, parallel to S1 with junction dots
dot(60, 180); dot(60, 150)
wire(60, 180, 85, 180); wire(85, 180, 85, 174)
no_contact(85, 162); text(91, 167, "-K1"); text(91, 163, "/2.3", 6)
wire(85, 162, 85, 150); wire(85, 150, 60, 150)
# rung 3: -K1 NO -> -H1 lamp
wire(60, 200, 130, 200); dot(60, 200)
wire(130, 200, 130, 180)
no_contact(130, 168); text(136, 173, "-K1")
wire(130, 168, 130, 160)
lamp(130, 148); text(136, 153, "-H1")
wire(130, 148, 130, 120); wire(130, 120, 60, 120); dot(60, 120)
text(126, 163, "12", 6)
c.showPage()
c.save()
print("wrote", out)
