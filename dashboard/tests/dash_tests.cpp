#include <QDir>
#include <QFile>
#include <QJsonDocument>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QtTest>

#include "backend.h"
#include "ledger.h"
#include "source.h"
#include "theme.h"
#include "translator.h"

static const QDateTime Now = QDateTime::fromString("2026-11-11T10:00:00.000Z", Qt::ISODateWithMs);
static const QStringList Fixtures{FIXTURES "/api.jsonl", FIXTURES "/web.jsonl"};

class DashTests : public QObject {
    Q_OBJECT

private slots:
    void initTestCase() { QQuickStyle::setStyle("Material"); }

    // ---- folding events
    void foldsOpenItemsLeasesAndHandoffs()
    {
        Ledger ledger;
        LedgerFiles files(&ledger, Fixtures);
        QCOMPARE(ledger.repos().size(), 2);
        QCOMPARE(ledger.openItems({}, "awaiting").size(), 2);
        QCOMPARE(ledger.openItems({}, "todo").size(), 2);   // the web todo was done
        const auto leases = ledger.leases({}, Now);
        QCOMPARE(leases.size(), 4);
        QCOMPARE(leases.last().glob, QString("src/auth/session.ts"));
        QCOMPARE(leases.last().state, QString("expired"));
        QVERIFY(leases.last().dirty);
        QCOMPARE(ledger.handoffs().first().text, QString("Tax rounding: half-even everywhere; tests pinned to the 2025 tables."));
        QCOMPARE(ledger.alerts().size(), 2);
    }

    void aLeaseDoneIsGoneAndSettlesHalfEditsUnderIt()
    {
        Ledger l;
        l.ingest("r", obj("{\"id\":\"e1\",\"type\":\"claim\",\"glob\":\"src/a.c\",\"ts\":\"2026-11-11T09:00:00.000Z\",\"expires\":\"2026-11-11T09:30:00.000Z\"}"));
        l.ingest("r", obj("{\"id\":\"e2\",\"type\":\"release\",\"ref\":\"e1\",\"ts\":\"2026-11-11T09:01:00.000Z\"}"));
        QCOMPARE(l.leases("r", Now).size(), 1);   // released, still dirty
        l.ingest("r", obj("{\"id\":\"e3\",\"type\":\"claim\",\"glob\":\"src/**\",\"ts\":\"2026-11-11T09:02:00.000Z\",\"expires\":\"2026-11-11T11:00:00.000Z\"}"));
        l.ingest("r", obj("{\"id\":\"e4\",\"type\":\"done\",\"ref\":\"e3\",\"ts\":\"2026-11-11T09:03:00.000Z\"}"));
        QVERIFY(l.leases("r", Now).isEmpty());
    }

    void aHeldLeasePastItsTimeShowsExpiredWithoutTheDaemon()
    {
        Ledger l;
        l.ingest("r", obj("{\"id\":\"e1\",\"type\":\"claim\",\"glob\":\"x\",\"ts\":\"2026-11-11T08:00:00.000Z\",\"expires\":\"2026-11-11T08:30:00.000Z\"}"));
        QCOMPARE(l.leases("r", Now).first().state, QString("expired"));
    }

    // ---- following files
    void followsAppendsAHalfLineAndAReplacedLedger()
    {
        QTemporaryDir dir;
        QDir(dir.path()).mkpath("repo/.sankofa");
        const QString ledgerPath = dir.path() + "/repo/.sankofa/ledger.jsonl";
        write(ledgerPath, "{\"id\":\"e1\",\"type\":\"todo\",\"text\":\"one\",\"ts\":\"2026-11-11T09:00:00.000Z\"}" "\n");
        write(dir.path() + "/watched", (dir.path() + "/repo\n").toUtf8());
        Ledger ledger;
        LedgerFiles files(&ledger, {}, dir.path() + "/watched");
        QCOMPARE(ledger.openItems().size(), 1);

        append(ledgerPath, "{\"id\":\"e2\",\"type\":\"todo\",\"text\":\"two\",\"ts\":\"2026-11-11T09:01:00.000Z\"}" "\n{\"id\":\"e3\",\"type\":\"to");
        QTRY_COMPARE(ledger.openItems().size(), 2);
        append(ledgerPath, "do\",\"text\":\"three\",\"ts\":\"2026-11-11T09:02:00.000Z\"}" "\n");
        QTRY_COMPARE(ledger.openItems().size(), 3);

        // a hub replica rewritten after a reconcile: read from the start
        write(ledgerPath + ".tmp", "{\"id\":\"e1\",\"type\":\"todo\",\"text\":\"only\",\"ts\":\"2026-11-11T09:00:00.000Z\"}" "\n");
        QFile::remove(ledgerPath);
        QFile::rename(ledgerPath + ".tmp", ledgerPath);
        QTRY_COMPARE(ledger.openItems().size(), 1);
        QCOMPARE(ledger.openItems().first().text, QString("only"));
    }

    void aWatchedRepoWithoutALedgerIsReported()
    {
        QTemporaryDir dir;
        write(dir.path() + "/watched", (dir.path() + "/nowhere\n").toUtf8());
        Ledger ledger;
        LedgerFiles files(&ledger, {}, dir.path() + "/watched");
        QCOMPARE(files.missing(), QStringList{dir.path() + "/nowhere"});
    }

    // ---- what the window gets
    void backendFiltersByRepoAndSaysHowLongAgo()
    {
        Ledger ledger;
        LedgerFiles files(&ledger, Fixtures);
        Backend b(&ledger, &files, Now);
        QCOMPARE(b.waiting().size(), 2);
        b.nextRepo();
        QCOMPARE(b.repoName(), QString("api"));
        QCOMPARE(b.waiting().size(), 1);
        QCOMPARE(b.waiting().first().toMap().value("command").toString(), QString("sankofa done e6"));
        b.nextRepo(); b.nextRepo();
        QCOMPARE(b.repoIndex(), -1);
        QCOMPARE(b.ago("2026-11-11T09:12:00.000Z"), QString("48 min ago"));
        QCOMPARE(b.ago("2026-11-11T10:22:00.000Z"), QString("in 22 min"));
        QCOMPARE(b.ago("2026-11-11T03:04:00.000Z"), QString("6 h ago"));
    }

    // ---- the window itself, from the fixture stream, with no daemon
    void rendersFromAFixtureWithoutADaemon()
    {
        Theme theme(QTemporaryDir().path());
        Translator translator(QDir::tempPath() + "/no-such-language", ":/i18n");
        Ledger ledger;
        LedgerFiles files(&ledger, Fixtures);
        Backend backend(&ledger, &files, Now);
        QQmlApplicationEngine engine;
        engine.rootContext()->setContextProperty("translator", &translator);
        engine.rootContext()->setContextProperty("theme", &theme);
        engine.rootContext()->setContextProperty("backend", &backend);
        QStringList warnings;
        connect(&engine, &QQmlApplicationEngine::warnings, this, [&](const QList<QQmlError> &w) {
            for (const auto &e : w) warnings << e.toString();
        });
        engine.load(QUrl("qrc:/Main.qml"));
        QCOMPARE(engine.rootObjects().size(), 1);
        auto *window = qobject_cast<QQuickWindow *>(engine.rootObjects().first());
        QVERIFY(window);
        QTest::qWait(300);
        QVERIFY2(warnings.isEmpty(), qPrintable(warnings.join("\n")));
        QStringList texts;
        collectTexts(window->contentItem(), texts);
        for (const char *want : {"Needs a decision: keep the v1 billing webhook for one more release, or drop it now?",
                                 "migrations/**", "half-finished", "Reports table virtualised; scroll stays at 60 fps with 10k rows.",
                                 "Remove the TAX_LEGACY flag once the migration has run", "overlap", "landed"})
            QVERIFY2(texts.contains(want), want);
        const QImage shot = window->grabWindow();
        QVERIFY(!shot.isNull());
    }

    void keysDriveTheWindow()
    {
        Theme theme(QTemporaryDir().path());
        Translator translator(QDir::tempPath() + "/no-such-language", ":/i18n");
        Ledger ledger;
        LedgerFiles files(&ledger, Fixtures);
        Backend backend(&ledger, &files, Now);
        QQmlApplicationEngine engine;
        engine.rootContext()->setContextProperty("translator", &translator);
        engine.rootContext()->setContextProperty("theme", &theme);
        engine.rootContext()->setContextProperty("backend", &backend);
        engine.load(QUrl("qrc:/Main.qml"));
        auto *window = qobject_cast<QQuickWindow *>(engine.rootObjects().first());
        window->show();
        window->requestActivate();
        QVERIFY(QTest::qWaitForWindowExposed(window));
        QSignalSpy copied(&backend, &Backend::copied);

        QTest::keyClick(window, Qt::Key_Tab);
        QCOMPARE(backend.repoName(), QString("api"));
        QTest::keyClick(window, Qt::Key_Tab, Qt::ShiftModifier);
        QCOMPARE(backend.repoIndex(), -1);
        // the human queue first: Down selects its newest item, Space copies how to resolve it
        QTest::keyClick(window, Qt::Key_Down);
        QTest::keyClick(window, Qt::Key_Space);
        QCOMPARE(copied.size(), 1);
        QCOMPARE(copied.first().first().toString(), QString("sankofa done e6"));
        // two panels on: who holds what; Space copies the lease's paths
        QTest::keyClick(window, Qt::Key_Right);
        QTest::keyClick(window, Qt::Key_Right);
        QTest::keyClick(window, Qt::Key_Space);
        QCOMPARE(copied.last().first().toString(), QString("migrations/**"));
        QTest::keyClick(window, '?');
        QVERIFY(window->property("helpVisible").toBool());
    }

    // ---- theme and language (from the Omarchy app template)
    void themeFallsBackAndReadsAccent()
    {
        QTemporaryDir dir;
        QCOMPARE(Theme(dir.path()).accent(), QString("#FFD60A"));
        QDir(dir.path()).mkpath("theme");
        write(dir.path() + "/theme/colors.toml", "accent = \"#112233\"\n");
        Theme theme(dir.path());
        QCOMPARE(theme.accent(), QString("#112233"));
        QCOMPARE(theme.accentForeground(), QString("white"));
    }

    void translatorFollowsTheLanguageFile()
    {
        QTemporaryDir dir;
        write(dir.path() + "/language", "da\n");
        Translator t(dir.path() + "/language", ":/i18n");
        QCOMPARE(t.language(), QString("da"));
        QCOMPARE(t.translate("Main", "Waiting on you"), QString("Venter på dig"));
        QVERIFY(t.translate("Main", "no such string").isNull());
    }

private:
    static QJsonObject obj(const char *json) { return QJsonDocument::fromJson(json).object(); }
    static void write(const QString &path, const QByteArray &contents)
    {
        QFile file(path);
        QVERIFY(file.open(QIODevice::WriteOnly));
        file.write(contents);
    }
    static void append(const QString &path, const QByteArray &contents)
    {
        QFile file(path);
        QVERIFY(file.open(QIODevice::Append));
        file.write(contents);
    }
    static void collectTexts(QQuickItem *item, QStringList &out)
    {
        const QVariant t = item->property("text");
        if (t.isValid() && !t.toString().isEmpty())
            out << t.toString();
        for (auto *c : item->childItems())
            collectTexts(c, out);
    }
};

QTEST_MAIN(DashTests)
#include "dash_tests.moc"
