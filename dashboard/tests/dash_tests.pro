QT += core gui qml quick quickcontrols2 testlib
CONFIG += c++17 testcase
TARGET = dash_tests
TEMPLATE = app

INCLUDEPATH += ../src
DEFINES += FIXTURES=\\\"$$PWD/fixtures\\\"

HEADERS += ../src/backend.h ../src/ledger.h ../src/source.h ../src/theme.h ../src/translator.h
SOURCES += dash_tests.cpp ../src/backend.cpp ../src/ledger.cpp ../src/source.cpp ../src/theme.cpp ../src/translator.cpp
RESOURCES += ../src/resources.qrc
