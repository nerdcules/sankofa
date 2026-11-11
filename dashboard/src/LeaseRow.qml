import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

// A lease: the paths, who holds them, until when; an ended one still marked half-finished.
ColumnLayout {
    spacing: 2
    readonly property var it: parent ? parent.item : null
    readonly property bool held: it && it.state === "held"
    RowLayout {
        Layout.fillWidth: true
        Label {
            text: it ? it.glob : ""
            color: "#e6e6ea"
            font.family: "monospace"
            elide: Text.ElideMiddle
            Layout.fillWidth: true
        }
        Label {
            visible: !held
            text: qsTr("half-finished")
            color: Material.color(Material.Orange, Material.Shade300)
            font.pixelSize: 12
        }
    }
    Label {
        Layout.fillWidth: true
        text: !it ? "" : held
              ? qsTr("%1 · %2 · expires %3").arg(it.who).arg(it.repo).arg(backend.ago(it.expires))
              : qsTr("%1 · %2 · %3 without done").arg(it.who).arg(it.repo).arg(it.state === "released" ? qsTr("released") : qsTr("expired"))
        color: "#7c7c84"
        font.pixelSize: 12
        elide: Text.ElideRight
    }
}
