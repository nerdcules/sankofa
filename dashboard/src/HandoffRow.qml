import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// The newest thing a session wrote down: what the next session starts from.
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
        text: it ? "%1  ·  %2  ·  %3".arg(it.who).arg(it.repo).arg(backend.ago(it.ts)) : ""
        color: "#7c7c84"
        font.pixelSize: 12
        elide: Text.ElideRight
    }
}
