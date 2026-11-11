QT += core gui qml quick quickcontrols2

CONFIG += c++17 release
TARGET = sankofa-dash
TEMPLATE = app

HEADERS += \
    src/backend.h \
    src/ledger.h \
    src/source.h \
    src/theme.h \
    src/translator.h

SOURCES += \
    src/main.cpp \
    src/backend.cpp \
    src/ledger.cpp \
    src/source.cpp \
    src/theme.cpp \
    src/translator.cpp

RESOURCES += src/resources.qrc
