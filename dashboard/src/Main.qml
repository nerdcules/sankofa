import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

ApplicationWindow {
    id: win
    width: 1180
    height: 760
    minimumWidth: 760
    minimumHeight: 480
    visible: true
    title: qsTr("Sankofa")
    color: "#0e0e10"

    Material.theme: Material.Dark
    Material.accent: theme.accent
    Material.roundedScale: theme.radius

    property bool helpVisible: false
    property int pane: 0                     // which panel the keys move in
    readonly property var panels: [waitingPanel, todoPanel, leasePanel, handoffPanel]
    property string toast: ""

    function focusPanel(i) { pane = (i + panels.length) % panels.length; if (panels[pane].currentIndex < 0) panels[pane].move(0) }
    function activate(p) {
        const it = p.current()
        if (!it) return
        backend.copy(it.command !== undefined ? it.command : it.glob)
    }

    Shortcut { sequences: ["?", "Shift+?"]; context: Qt.ApplicationShortcut; onActivated: win.helpVisible = !win.helpVisible }
    Shortcut { sequence: "Q"; context: Qt.ApplicationShortcut; onActivated: Qt.quit() }
    Shortcut { sequence: "Tab"; context: Qt.ApplicationShortcut; onActivated: backend.nextRepo() }
    Shortcut { sequence: "Shift+Tab"; context: Qt.ApplicationShortcut; onActivated: backend.previousRepo() }
    Shortcut { sequences: ["Right", "L"]; context: Qt.ApplicationShortcut; onActivated: win.focusPanel(win.pane + 1) }
    Shortcut { sequences: ["Left", "H"]; context: Qt.ApplicationShortcut; onActivated: win.focusPanel(win.pane - 1) }
    Shortcut { sequences: ["Down", "J"]; context: Qt.ApplicationShortcut; onActivated: win.panels[win.pane].move(1) }
    Shortcut { sequences: ["Up", "K"]; context: Qt.ApplicationShortcut; onActivated: win.panels[win.pane].move(-1) }
    Shortcut { sequence: "Space"; context: Qt.ApplicationShortcut; onActivated: win.activate(win.panels[win.pane]) }
    Shortcut { sequence: "R"; context: Qt.ApplicationShortcut; onActivated: backend.refresh() }
    Shortcut { sequence: "Escape"; context: Qt.ApplicationShortcut; onActivated: win.helpVisible = false }

    Connections {
        target: backend
        function onCopied(text) { win.toast = qsTr("Copied: %1").arg(text); toastTimer.restart() }
    }
    Timer { id: toastTimer; interval: 2200; onTriggered: win.toast = "" }

    component Meta: Label {
        color: "#7c7c84"
        font.pixelSize: 12
        elide: Text.ElideRight
    }

    Item {
        anchors.fill: parent
        LayoutMirroring.enabled: Qt.application.layoutDirection === Qt.RightToLeft
        LayoutMirroring.childrenInherit: true

        ColumnLayout {
            anchors.fill: parent
            anchors.margins: 16
            spacing: 12

            // header: the repos, and how fresh the view is
            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                Label {
                    text: "sankofa"
                    font.pixelSize: 20
                    font.weight: Font.Bold
                    color: "#ececf0"
                    Layout.rightMargin: 10
                }
                Repeater {
                    model: [{ key: "", name: qsTr("All repos"), waiting: backend.waiting.length, index: -1 }]
                           .concat(backend.repos.map((r, i) => ({ key: r.key, name: r.name, waiting: r.waiting, index: i })))
                    Rectangle {
                        required property var modelData
                        readonly property bool active: backend.repoIndex === modelData.index
                        radius: Math.min(4, theme.radius)
                        color: active ? theme.accent : "#18181c"
                        border.color: active ? theme.accent : "#2a2a30"
                        implicitHeight: 30
                        implicitWidth: chip.implicitWidth + 22
                        RowLayout {
                            id: chip
                            anchors.centerIn: parent
                            spacing: 6
                            Label {
                                text: modelData.name
                                color: active ? theme.accentForeground : "#d0d0d6"
                                font.weight: active ? Font.DemiBold : Font.Normal
                            }
                            Label {
                                visible: modelData.waiting > 0 && modelData.index >= 0
                                text: "●"
                                color: active ? theme.accentForeground : theme.accent
                                font.pixelSize: 9
                            }
                        }
                        TapHandler { onTapped: backend.repoIndex = modelData.index }
                    }
                }
                Item { Layout.fillWidth: true }
                Meta {
                    text: backend.lastEvent === "" ? "" :
                          qsTr("%n events, last %1", "", backend.events).arg(backend.ago(backend.lastEvent))
                }
                Meta { text: "  ?  " + qsTr("keys"); color: "#5c5c64" }
            }

            Label {
                visible: backend.missing.length > 0
                Layout.fillWidth: true
                text: qsTr("No ledger at %1. Run sankofa init there, or sankofa watch rm to stop listing it.").arg(backend.missing.join(", "))
                color: Material.color(Material.Orange)
                wrapMode: Text.WordWrap
            }

            // nothing to show yet
            ColumnLayout {
                visible: !backend.watching
                Layout.fillWidth: true
                Layout.fillHeight: true
                Item { Layout.fillHeight: true }
                Label {
                    Layout.alignment: Qt.AlignHCenter
                    text: qsTr("No ledgers to show")
                    font.pixelSize: 22
                    color: "#d0d0d6"
                }
                Label {
                    Layout.alignment: Qt.AlignHCenter
                    text: qsTr("In a repo with a sankofa ledger, run sankofa watch add, or name repos when starting sankofa-dash.")
                    color: "#7c7c84"
                }
                Item { Layout.fillHeight: true }
            }

            GridLayout {
                visible: backend.watching
                Layout.fillWidth: true
                Layout.fillHeight: true
                columns: 2
                rowSpacing: 12
                columnSpacing: 12

                LedgerPanel {
                    id: waitingPanel
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.preferredHeight: 3
                    title: qsTr("Waiting on you")
                    loud: true
                    focused: win.pane === 0
                    rows: backend.waiting
                    empty: qsTr("Nothing waits on you.")
                    onActivated: (it) => { win.pane = 0; backend.copy(it.command) }
                    row: ItemRow {}
                }
                LedgerPanel {
                    id: leasePanel
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.preferredHeight: 3
                    title: qsTr("Who holds what")
                    focused: win.pane === 2
                    rows: backend.leases
                    empty: qsTr("No paths are leased.")
                    onActivated: (it) => { win.pane = 2; backend.copy(it.glob) }
                    row: LeaseRow {}
                }
                LedgerPanel {
                    id: todoPanel
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.preferredHeight: 4
                    title: qsTr("Open")
                    focused: win.pane === 1
                    rows: backend.todos
                    empty: qsTr("No open items.")
                    onActivated: (it) => { win.pane = 1; backend.copy(it.command) }
                    row: ItemRow {}
                }
                LedgerPanel {
                    id: handoffPanel
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.preferredHeight: 4
                    title: qsTr("Last handoff per session")
                    focused: win.pane === 3
                    rows: backend.handoffs
                    empty: qsTr("No session has written a handoff yet.")
                    row: HandoffRow {}
                }
            }

            // what happened without anyone asking: overlaps, landings, flags
            Rectangle {
                visible: backend.watching && backend.alerts.length > 0
                Layout.fillWidth: true
                implicitHeight: alertsCol.implicitHeight + 20
                color: "#121215"
                radius: theme.radius
                border.color: "#24242a"
                ColumnLayout {
                    id: alertsCol
                    anchors.fill: parent
                    anchors.margins: 10
                    spacing: 3
                    Repeater {
                        model: backend.alerts.slice(0, 4)
                        RowLayout {
                            required property var modelData
                            Layout.fillWidth: true
                            spacing: 10
                            Label {
                                text: modelData.type === "overlap" ? qsTr("overlap")
                                    : modelData.type === "landed" ? qsTr("landed")
                                    : modelData.type === "contend" ? qsTr("contention") : qsTr("flag")
                                color: modelData.type === "landed" ? Material.color(Material.Green, Material.Shade300)
                                     : modelData.type === "flag" ? Material.color(Material.Orange, Material.Shade300)
                                     : theme.accent
                                font.pixelSize: 12
                                font.weight: Font.DemiBold
                                Layout.minimumWidth: 84
                            }
                            Meta { text: modelData.repo; Layout.preferredWidth: 90 }
                            Label { text: modelData.text; color: "#c0c0c6"; elide: Text.ElideRight; Layout.fillWidth: true; font.pixelSize: 12 }
                            Meta { text: backend.ago(modelData.ts) }
                        }
                    }
                }
            }
        }

        Rectangle {
            visible: win.toast !== ""
            anchors.horizontalCenter: parent.horizontalCenter
            anchors.bottom: parent.bottom
            anchors.bottomMargin: 24
            radius: Math.min(4, theme.radius)
            color: theme.accent
            implicitWidth: toastLabel.implicitWidth + 28
            implicitHeight: toastLabel.implicitHeight + 14
            Label { id: toastLabel; anchors.centerIn: parent; text: win.toast; color: theme.accentForeground }
        }

        HelpOverlay { anchors.fill: parent; visible: win.helpVisible }
    }
}
