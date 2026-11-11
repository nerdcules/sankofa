// sankofa-dash: who holds what, what is open, what waits on you, across your sankofa ledgers.
//
//   sankofa-dash [open] [repo...]          the window (default: the repos sankofa watch lists)
//   sankofa-dash summary [--json] [repo...] the same, as text, with no display
//   sankofa-dash render --out FILE [--now ISO] [--size WxH] [repo...]
//                                          a picture of the window, with no display
//   sankofa-dash version

#include <QCommandLineParser>
#include <QGuiApplication>
#include <QIcon>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTimer>
#include <QUrl>
#include <cstdio>

#include "backend.h"
#include "ledger.h"
#include "source.h"
#include "theme.h"
#include "translator.h"

static const char *Version = "0.8.0";

static int summary(Backend &b, bool json)
{
    if (json) {
        QJsonObject o;
        for (const char *k : {"waiting", "todos", "leases", "handoffs", "alerts"})
            o.insert(k, QJsonArray::fromVariantList(b.property(k).toList()));
        o.insert("repos", QJsonArray::fromVariantList(b.repos()));
        o.insert("missing", QJsonArray::fromStringList(b.missing()));
        std::fputs(QJsonDocument(o).toJson(QJsonDocument::Indented).constData(), stdout);
        return 0;
    }
    auto section = [&](const char *title, const QVariantList &rows, auto line) {
        std::printf("%s (%d)\n", title, int(rows.size()));
        for (const auto &r : rows) std::printf("  %s\n", line(r.toMap()).toUtf8().constData());
    };
    section("Waiting on you", b.waiting(), [&](const QVariantMap &m) {
        return QString("%1  %2  %3  (%4, %5)").arg(m["id"].toString(), m["repo"].toString(), m["text"].toString(), m["who"].toString(), b.ago(m["ts"].toString())); });
    section("Open", b.todos(), [&](const QVariantMap &m) {
        return QString("%1  %2  %3  (%4)").arg(m["id"].toString(), m["repo"].toString(), m["text"].toString(), b.ago(m["ts"].toString())); });
    section("Leases", b.leases(), [&](const QVariantMap &m) {
        return QString("%1  %2  %3  %4%5  %6").arg(m["id"].toString(), m["repo"].toString(), m["glob"].toString(), m["state"].toString(),
                                                   m["dirty"].toBool() && m["state"] != "held" ? ",dirty" : "", m["who"].toString()); });
    section("Last handoffs", b.handoffs(), [&](const QVariantMap &m) {
        return QString("%1  %2  %3: %4").arg(b.ago(m["ts"].toString()), m["repo"].toString(), m["who"].toString(), m["text"].toString()); });
    for (const auto &m : b.missing()) std::printf("missing ledger: %s\n", m.toUtf8().constData());
    return 0;
}

int main(int argc, char *argv[])
{
    // the commands that need no display must not ask for one
    const QByteArray first = argc > 1 ? QByteArray(argv[1]) : QByteArray();
    const bool headless = first == "summary" || first == "render" || first == "version" || first == "--version";
    if (headless)
        qputenv("QT_QPA_PLATFORM", "offscreen");

    QGuiApplication app(argc, argv);
    app.setApplicationName("sankofa-dash");
    app.setApplicationVersion(Version);
    app.setDesktopFileName("sankofa-dash");
    app.setWindowIcon(QIcon::fromTheme("sankofa-dash"));
    QQuickStyle::setStyle("Material");

    QCommandLineParser parser;
    parser.setApplicationDescription("Who holds what, what is open, and what waits on you, across sankofa ledgers.");
    parser.addHelpOption();
    parser.addPositionalArgument("command", "open (default), summary, render, version");
    parser.addPositionalArgument("repo", "repo roots or ledger files (default: the repos sankofa watch lists)", "[repo...]");
    QCommandLineOption json("json", "summary: as JSON");
    QCommandLineOption out("out", "render: the PNG to write", "file");
    QCommandLineOption now("now", "a fixed time to show (render, tests), ISO 8601", "time");
    QCommandLineOption size("size", "render: window size", "WxH", "1180x760");
    QCommandLineOption registry("registry", "the watched-repos file to read", "file");
    QCommandLineOption language("language-file", "read the language from this file instead of Omarchy's (screenshots, tests)", "file");
    QCommandLineOption help("show-keys", "render: with the keys overlay open");
    parser.addOptions({json, out, now, size, registry, language, help});
    parser.process(app);

    QStringList args = parser.positionalArguments();
    QString command = "open";
    if (!args.isEmpty() && QStringList{"open", "summary", "render", "version"}.contains(args.first()))
        command = args.takeFirst();
    if (command == "version") { std::printf("sankofa-dash %s\n", Version); return 0; }

    Translator translator(parser.value(language));
    app.installTranslator(&translator);
    Theme theme;
    Ledger ledger;
    LedgerFiles files(&ledger, args, parser.value(registry));
    const QDateTime fixed = parser.isSet(now) ? QDateTime::fromString(parser.value(now), Qt::ISODateWithMs) : QDateTime();
    Backend backend(&ledger, &files, fixed);

    if (command == "summary")
        return summary(backend, parser.isSet(json));

    QQmlApplicationEngine engine;
    QObject::connect(&translator, &Translator::changed, &engine, &QQmlEngine::retranslate);
    engine.rootContext()->setContextProperty("translator", &translator);
    engine.rootContext()->setContextProperty("theme", &theme);
    engine.rootContext()->setContextProperty("backend", &backend);
    engine.load(QUrl("qrc:/Main.qml"));
    if (engine.rootObjects().isEmpty())
        return 1;

    if (command == "render") {
        if (!parser.isSet(out)) { std::fputs("sankofa-dash render: --out FILE is required\n", stderr); return 2; }
        auto *window = qobject_cast<QQuickWindow *>(engine.rootObjects().first());
        const auto wh = parser.value(size).split('x');
        if (wh.size() == 2) window->resize(wh[0].toInt(), wh[1].toInt());
        if (parser.isSet(help)) window->setProperty("helpVisible", true);
        backend.refresh();
        int code = 1;
        QTimer::singleShot(400, &app, [&] {
            code = window->grabWindow().save(parser.value(out)) ? 0 : 1;
            if (code) std::fprintf(stderr, "sankofa-dash render: could not write %s\n", parser.value(out).toUtf8().constData());
            app.exit(code);
        });
        return app.exec();
    }
    return app.exec();
}
