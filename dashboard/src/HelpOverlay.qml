import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

// Every shortcut, shown with ?.
Rectangle {
    id: overlay
    color: Qt.rgba(0, 0, 0, 0.72)
    TapHandler { onTapped: overlay.visible = false }

    readonly property var keys: [
        ["Tab", qsTr("Next repo (or all of them)")],
        ["Shift+Tab", qsTr("Previous repo")],
        ["← →  H L", qsTr("Move between panels")],
        ["↑ ↓  K J", qsTr("Move within a panel")],
        ["Space", qsTr("Copy: the command that resolves an item, or a lease's paths")],
        ["R", qsTr("Read the ledgers again")],
        ["?", qsTr("Show or hide these keys")],
        ["Q", qsTr("Quit")],
    ]

    Rectangle {
        anchors.centerIn: parent
        width: Math.min(parent.width - 48, grid.implicitWidth + 56)
        height: grid.implicitHeight + 80
        color: "#18181b"
        radius: theme.radius
        border.color: "#2a2a30"
        ColumnLayout {
            anchors.fill: parent
            anchors.margins: 28
            spacing: 16
            Label { text: qsTr("Keys"); font.pixelSize: 18; font.weight: Font.DemiBold; color: "#ececf0" }
            GridLayout {
                id: grid
                columns: 2
                columnSpacing: 24
                rowSpacing: 8
                Repeater {
                    model: overlay.keys.length * 2
                    Label {
                        required property int index
                        readonly property var pair: overlay.keys[Math.floor(index / 2)]
                        text: index % 2 === 0 ? pair[0] : pair[1]
                        color: index % 2 === 0 ? theme.accent : "#d0d0d6"
                        font.family: index % 2 === 0 ? "monospace" : Qt.application.font.family
                    }
                }
            }
        }
    }
}
