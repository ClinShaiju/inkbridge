import QtQuick 2.15
import net.asivery.AppLoad 1.0

// inkbridge on-device visualizer (read-only).
//
// OpenTabletDriver on the Windows host owns ALL configuration (active area, aspect lock,
// rotation, pressure). This app only MIRRORS the live mapping so the user, looking at
// the blank e-ink sheet, can see where on the physical surface the pen is active. The rMPP screen
// IS the digitizer surface, so the box is drawn 1:1 (full-bleed) over the real region.
//
// Orientation: the surface has a long axis (239 mm) and short axis (179 mm). The box is laid out
// so the surface long axis runs along the screen's LONGER edge — i.e. it follows however the
// device/window is oriented. Held in landscape, the active region (full long axis, ~75% short
// axis here) shows as a WIDE box with free strips top & bottom; the stats sit in the top strip,
// never over the writing area. The app never reads evdev.
Item {
    id: root
    anchors.fill: parent

    signal close            // required AppLoad lifecycle (close handled natively by AppLoad)
    function unloading() {}

    // ── E-ink palette ──
    readonly property color cBg:     "#FFFFFF"
    readonly property color cFg:     "#000000"
    readonly property color cDead:   "#E4E4E4"   // surface the pen can't reach
    readonly property color cActive: "#FFFFFF"   // usable region
    readonly property color cDim:    "#666666"

    // ── Live model (defaults render immediately; backend refines) ──
    property var surface: ({ max_x: 11180, max_y: 15340, width_mm: 179.0, height_mm: 239.0 })
    property var area:    ({ width_mm: 239.0, height_mm: 134.4375, x_mm: 89.5, y_mm: 119.5, rotation: 270 })
    property var display: ({ width_px: 1920, height_px: 1080 })
    property bool connected: false
    property real latencyMs: -1
    property real rateHz:    -1

    // ── Surface axes (long = the device's long edge) ──
    // OTD area X/x_mm runs along surface.width_mm, Y/y_mm along surface.height_mm. On the rMPP
    // width_mm(179) is the short axis and height_mm(239) the long axis.
    readonly property real longMm:  Math.max(surface.width_mm, surface.height_mm)
    readonly property real shortMm: Math.min(surface.width_mm, surface.height_mm)
    readonly property real longC:   area.y_mm   // active-area centre along the long axis
    readonly property real shortC:  area.x_mm   // active-area centre along the short axis
    // rotation 90/270 swaps which area dimension lies along which surface axis
    readonly property bool rotated: (Math.round(area.rotation) % 180) === 90
    readonly property real fpLong:  rotated ? area.width_mm  : area.height_mm  // extent along long axis
    readonly property real fpShort: rotated ? area.height_mm : area.width_mm   // extent along short axis
    readonly property real pctUsed: (longMm * shortMm) > 0 ? 100 * (fpLong * fpShort) / (longMm * shortMm) : 0

    // ── Map onto the window: long axis -> longer screen edge (1:1, full-bleed) ──
    readonly property bool winLandscape: width >= height
    readonly property real _longSpan:  winLandscape ? width  : height   // px spanning the long axis
    readonly property real _shortSpan: winLandscape ? height : width    // px spanning the short axis
    readonly property real boxLongPx:  fpLong  / longMm  * _longSpan
    readonly property real boxShortPx: fpShort / shortMm * _shortSpan
    readonly property real boxLongCtr:  longC  / longMm  * _longSpan
    readonly property real boxShortCtr: shortC / shortMm * _shortSpan
    readonly property real boxX: (winLandscape ? boxLongCtr  : boxShortCtr) - (winLandscape ? boxLongPx  : boxShortPx) / 2
    readonly property real boxY: (winLandscape ? boxShortCtr : boxLongCtr)  - (winLandscape ? boxShortPx : boxLongPx)  / 2
    readonly property real boxW: winLandscape ? boxLongPx  : boxShortPx
    readonly property real boxH: winLandscape ? boxShortPx : boxLongPx

    // ── Backend link (AppLoad message types are INTEGERS) ──
    readonly property int mGetConfig: 1
    readonly property int mGetStatus: 2
    readonly property int mConfig:    1
    readonly property int mStatus:    2

    AppLoad {
        id: ipc
        applicationID: "com.inkbridge.companion"
        onMessageReceived: (type, contents) => root.onBackendMessage(type, contents)
    }
    function onBackendMessage(type, contents) {
        var data
        try { data = JSON.parse(contents) } catch (e) { return }
        if (type === root.mConfig) {
            if (data.surface) root.surface = data.surface
            if (data.area)    root.area    = data.area
            if (data.display) root.display = data.display
        } else if (type === root.mStatus) {
            // status now carries the live PC↔device link (from the OTD plugin)
            if (data.connected !== undefined)  root.connected = data.connected
            if (data.latency_ms !== undefined) root.latencyMs = data.latency_ms
            if (data.rate_hz !== undefined)    root.rateHz    = data.rate_hz
        }
    }
    Component.onCompleted: {
        ipc.sendMessage(root.mGetConfig, "")
        ipc.sendMessage(root.mGetStatus, "")
    }

    // ── Dead surface (full-bleed; the box sits on it 1:1) ──
    Rectangle { anchors.fill: parent; color: cDead }

    // ── Active-area box ──
    Rectangle {
        id: box
        x: root.boxX; y: root.boxY
        width: root.boxW; height: root.boxH
        color: cActive
        border.color: cFg
        border.width: 4

        Repeater {
            model: [[0,0],[1,0],[0,1],[1,1]]
            Item {
                readonly property bool rx: modelData[0] === 1
                readonly property bool ry: modelData[1] === 1
                x: rx ? box.width  - 60 : 0
                y: ry ? box.height - 60 : 0
                Rectangle { width: 60; height: 8; color: cFg; x: 0; y: ry ? 52 : 0 }
                Rectangle { width: 8; height: 60; color: cFg; x: rx ? 52 : 0; y: 0 }
            }
        }
        Rectangle { color: cDim; width: 2; height: 40; anchors.centerIn: parent }
        Rectangle { color: cDim; width: 40; height: 2; anchors.centerIn: parent }
    }

    // ── Title: centred in the top free strip (above the box) ──
    Item {
        anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top
        height: Math.max(110, root.boxY)
        Text {
            anchors.centerIn: parent
            text: "inkbridge"; color: cFg
            font.pixelSize: 52; font.bold: true; font.letterSpacing: 3
        }
    }

    // ── Connection: bottom-left free strip ──
    Row {
        anchors.left: parent.left; anchors.bottom: parent.bottom
        anchors.leftMargin: 44; anchors.bottomMargin: 40
        spacing: 14
        Rectangle {
            width: 28; height: 28; radius: 14; anchors.verticalCenter: parent.verticalCenter
            color: root.connected ? cFg : cBg; border.color: cFg; border.width: 3
        }
        Text {
            text: root.connected ? "Connected" : "Disconnected"
            color: cFg; font.pixelSize: 38; font.bold: true
            anchors.verticalCenter: parent.verticalCenter
        }
    }

    // ── Debug info: bottom-right free strip ──
    // Each field has a FIXED width + right alignment so a digit change (e.g. Hz 99->100) can't
    // resize its box and shove the neighbouring fields around (no more latency reflow).
    Row {
        anchors.right: parent.right; anchors.bottom: parent.bottom
        anchors.rightMargin: 44; anchors.bottomMargin: 40
        spacing: 30
        Text { width: 230; horizontalAlignment: Text.AlignRight; color: cDim; font.pixelSize: 32
               anchors.verticalCenter: parent.verticalCenter
               text: Math.round(root.fpLong) + " × " + Math.round(root.fpShort) + " mm" }
        Text { width: 160; horizontalAlignment: Text.AlignRight; color: cDim; font.pixelSize: 32
               anchors.verticalCenter: parent.verticalCenter
               text: Math.round(root.pctUsed) + "% used" }
        Text { width: 170; horizontalAlignment: Text.AlignRight; color: cDim; font.pixelSize: 32
               anchors.verticalCenter: parent.verticalCenter
               text: (root.latencyMs >= 0 ? root.latencyMs.toFixed(1) + " ms" : "— ms") }
        Text { width: 130; horizontalAlignment: Text.AlignRight; color: cDim; font.pixelSize: 32
               anchors.verticalCenter: parent.verticalCenter
               text: (root.rateHz >= 0 ? Math.round(root.rateHz) + " Hz" : "— Hz") }
    }
}
