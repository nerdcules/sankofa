#include "backend.h"

#include "source.h"

#include <QClipboard>
#include <QDir>
#include <QFileInfo>
#include <QGuiApplication>

Backend::Backend(Ledger *ledger, LedgerFiles *files, const QDateTime &now, QObject *parent)
    : QObject(parent), m_ledger(ledger), m_files(files), m_now(now)
{
    // a burst of events (a reconcile, a replayed ledger) becomes one rebuild
    m_coalesce.setSingleShot(true);
    m_coalesce.setInterval(30);
    connect(&m_coalesce, &QTimer::timeout, this, &Backend::rebuild);
    connect(m_ledger, &Ledger::changed, this, [this] { m_coalesce.start(); });
    if (m_files)
        connect(m_files, &LedgerFiles::changed, this, [this] { m_coalesce.start(); });
    // leases expire and "5 min ago" moves on without any event
    m_tick.setInterval(15000);
    connect(&m_tick, &QTimer::timeout, this, &Backend::rebuild);
    if (!m_now.isValid())
        m_tick.start();
    rebuild();
}

QString Backend::shortName(const QString &root, const QStringList &all)
{
    // a repo root names itself; a ledger file is named after its repo (repo/.sankofa/ledger.jsonl)
    // or, for a loose file like a fixture, after itself
    auto base = [](const QString &r) {
        if (!r.endsWith(".jsonl"))
            return QFileInfo(r).fileName();
        const QFileInfo f(r);
        return f.absolutePath().endsWith("/.sankofa") ? QFileInfo(f.absolutePath()).dir().dirName() : f.completeBaseName();
    };
    const QString name = base(root);
    int same = 0;
    for (const auto &o : all)
        same += base(o) == name;
    return same > 1 ? QFileInfo(root).dir().dirName() + "/" + name : name;
}

QVariantList Backend::repos() const
{
    QVariantList out;
    const auto all = m_ledger->repos();
    for (const auto &r : all)
        out << QVariantMap{{"key", r}, {"name", shortName(r, all)},
                           {"waiting", int(m_ledger->openItems(r, "awaiting").size() + m_ledger->openItems(r, "handoff").size())}};
    return out;
}

void Backend::setRepoIndex(int i)
{
    if (i < -1 || i >= int(m_ledger->repos().size()) || i == m_repoIndex)
        return;
    m_repoIndex = i;
    rebuild();
}

QString Backend::current() const
{
    const auto all = m_ledger->repos();
    return m_repoIndex >= 0 && m_repoIndex < all.size() ? all[m_repoIndex] : QString();
}

QString Backend::repoName() const
{
    const QString r = current();
    return r.isEmpty() ? tr("All repos") : shortName(r, m_ledger->repos());
}

QStringList Backend::missing() const { return m_files ? m_files->missing() : QStringList(); }
int Backend::events() const { return m_ledger->eventCount(current()); }
QString Backend::lastEvent() const { return m_ledger->lastTs(current()); }
bool Backend::watching() const { return !m_ledger->repos().isEmpty() || (m_files && !m_files->roots().isEmpty()); }

void Backend::copy(const QString &text)
{
    if (auto *c = QGuiApplication::clipboard())
        c->setText(text);
    emit copied(text);
}

void Backend::refresh()
{
    if (m_files)
        m_files->poll();
    rebuild();
}

QString Backend::ago(const QString &ts) const
{
    const QDateTime t = QDateTime::fromString(ts, Qt::ISODateWithMs);
    if (!t.isValid())
        return ts;
    const qint64 s = t.secsTo(now());
    if (s < 0) {
        const qint64 m = -s / 60;
        return m < 60 ? tr("in %n min", "", int(m)) : tr("in %n h", "", int(m / 60));
    }
    if (s < 60) return tr("just now");
    if (s < 3600) return tr("%n min ago", "", int(s / 60));
    if (s < 86400) return tr("%n h ago", "", int(s / 3600));
    return tr("%n d ago", "", int(s / 86400));
}

void Backend::rebuild()
{
    const QString repo = current();
    const auto all = m_ledger->repos();
    auto who = [](const QString &session, const QString &agent, const QString &device) {
        QString s = agent == "main" ? session : agent;
        if (s.size() > 28) s = s.left(12) + "…" + s.right(8);
        return device.isEmpty() ? s : s + " @" + device;
    };
    auto items = [&](const QString &type) {
        QVariantList out;
        for (const auto &it : m_ledger->openItems(repo, type))
            out << QVariantMap{{"id", it.id}, {"type", it.type}, {"text", it.text}, {"ts", it.ts},
                               {"repo", shortName(it.repo, all)}, {"who", who(it.session, it.agent, it.device)},
                               {"command", QString(it.type == "handoff" ? "sankofa pickup %1" : "sankofa done %1").arg(it.id)}};
        return out;
    };
    // a handoff waits on whoever picks it up, like an awaiting item waits on the human
    m_waiting = items("awaiting") + items("handoff");
    m_todos = items("todo");

    m_leases.clear();
    for (const auto &l : m_ledger->leases(repo, now()))
        m_leases << QVariantMap{{"id", l.id}, {"glob", l.glob}, {"state", l.state}, {"dirty", l.dirty},
                                {"expires", l.expires}, {"claimed", l.claimed}, {"repo", shortName(l.repo, all)},
                                {"who", who(l.session, l.agent, l.device)}};
    m_handoffs.clear();
    for (const auto &h : m_ledger->handoffs(repo))
        m_handoffs << QVariantMap{{"session", h.session}, {"type", h.type}, {"text", h.text}, {"ts", h.ts},
                                  {"repo", shortName(h.repo, all)}, {"who", who(h.session, h.agent, h.device)}};
    m_alerts.clear();
    for (const auto &a : m_ledger->alerts(repo, 12))
        m_alerts << QVariantMap{{"id", a.id}, {"type", a.type}, {"text", a.text}, {"ts", a.ts},
                                {"repo", shortName(a.repo, all)}};
    emit changed();
}
