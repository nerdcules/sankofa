import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

// One titled list: a header with a count, rows, and a line for when it is empty.
Rectangle {
    id: panel
    property string title
    property var rows: []
    property string empty
    property bool focused: false
    property bool loud: false          // the human queue: drawn in the accent when it has rows
    // the selection is the panel's own: ListView resets its currentIndex whenever the rows change
    property int currentIndex: -1
    onRowsChanged: if (currentIndex >= rows.length) currentIndex = rows.length - 1
    property Component row
    signal activated(var item)

    color: "#151518"
    radius: theme.radius
    border.width: 1
    border.color: focused ? theme.accent : "#24242a"

    function move(d) {
        if (rows.length === 0) return
        currentIndex = Math.max(0, Math.min(rows.length - 1, currentIndex + d))
        list.positionViewAtIndex(currentIndex, ListView.Contain)
    }
    function current() { return currentIndex >= 0 && currentIndex < rows.length ? rows[currentIndex] : null }

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 14
        spacing: 8

        RowLayout {
            Layout.fillWidth: true
            Label {
                text: panel.title
                font.pixelSize: 13
                font.weight: Font.DemiBold
                font.capitalization: Font.AllUppercase
                font.letterSpacing: 1.2
                color: panel.loud && panel.rows.length > 0 ? theme.accent : "#9a9aa2"
            }
            Item { Layout.fillWidth: true }
            Rectangle {
                visible: panel.rows.length > 0
                radius: Math.min(4, theme.radius)
                color: panel.loud ? theme.accent : "#24242a"
                implicitWidth: countLabel.implicitWidth + 12
                implicitHeight: countLabel.implicitHeight + 4
                Label {
                    id: countLabel
                    anchors.centerIn: parent
                    text: panel.rows.length.toLocaleString(Qt.locale(), 'f', 0)
                    font.pixelSize: 12
                    font.weight: Font.DemiBold
                    color: panel.loud ? theme.accentForeground : "#c8c8ce"
                }
            }
        }

        ListView {
            id: list
            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            model: panel.rows
            spacing: 2
            boundsBehavior: Flickable.StopAtBounds
            keyNavigationEnabled: false   // the window's shortcuts move the selection, once
            ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
            delegate: Item {
                id: wrap
                width: ListView.view.width
                height: content.implicitHeight + 12
                required property var modelData
                required property int index
                Rectangle {
                    anchors.fill: parent
                    radius: Math.min(4, theme.radius)
                    color: panel.currentIndex === wrap.index && panel.focused ? Qt.rgba(1, 1, 1, 0.07)
                         : hover.hovered ? Qt.rgba(1, 1, 1, 0.04) : "transparent"
                }
                Loader {
                    id: content
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.verticalCenter: parent.verticalCenter
                    anchors.leftMargin: 8
                    anchors.rightMargin: 8
                    sourceComponent: panel.row
                    property var item: wrap.modelData
                }
                HoverHandler { id: hover }
                TapHandler {
                    onTapped: { panel.currentIndex = wrap.index; panel.activated(wrap.modelData) }
                }
            }
        }

        Label {
            visible: panel.rows.length === 0
            Layout.fillWidth: true
            Layout.fillHeight: true
            text: panel.empty
            color: "#6a6a72"
            wrapMode: Text.WordWrap
            verticalAlignment: Text.AlignTop
        }
    }
}
