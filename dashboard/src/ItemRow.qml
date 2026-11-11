import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// A todo or awaiting item: its text, then id, repo, who and when.
ColumnLayout {
    spacing: 2
    readonly property var it: parent ? parent.item : null
    Label {
        Layout.fillWidth: true
        text: it ? it.text : ""
        color: "#e6e6ea"
        wrapMode: Text.WordWrap
        maximumLineCount: 3
        elide: Text.ElideRight
    }
    Label {
        Layout.fillWidth: true
        text: it ? "%1  ·  %2  ·  %3  ·  %4".arg(it.id).arg(it.repo).arg(it.who).arg(backend.ago(it.ts)) : ""
        color: "#7c7c84"
        font.pixelSize: 12
        elide: Text.ElideRight
    }
}
