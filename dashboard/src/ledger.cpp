#include "ledger.h"

#include <algorithm>

void Ledger::resetRepo(const QString &repo)
{
    m_repos.remove(repo);
    emit changed();
}

void Ledger::ingest(const QString &repo, const QJsonObject &e)
{
    Repo &r = m_repos[repo];
    const QString type = e.value("type").toString(), id = e.value("id").toString(), ref = e.value("ref").toString();
    const QString ts = e.value("ts").toString(), text = e.value("text").toString();
    const QString session = e.value("session").toString("-"), agent = e.value("agent").toString("main");
    const QString device = e.value("device").toString();
    ++r.events;
    if (ts > r.lastTs)
        r.lastTs = ts;

    if (type == "todo" || type == "awaiting") {
        Item item{repo, id, type, text, ts, session, agent, device, false, {}, {}};
        r.items.insert(id, item);
    } else if (type == "claim") {
        Lease l{repo, id, e.value("glob").toString(), session, agent, device, ts, e.value("expires").toString()};
        r.leases.insert(id, l);
    } else if (type == "renew" && r.leases.contains(ref)) {
        r.leases[ref].expires = e.value("expires").toString();
    } else if (type == "release" && r.leases.contains(ref)) {
        r.leases[ref].state = "released";
    } else if (type == "expire" && r.leases.contains(ref)) {
        r.leases[ref].state = "expired";
    } else if (type == "done") {
        if (r.items.contains(ref)) {
            Item &it = r.items[ref];
            it.resolved = true;
            it.resolvedTs = ts;
            it.resolution = text;
        } else if (r.leases.contains(ref)) {
            Lease &l = r.leases[ref];
            l.state = "done";
            l.dirty = false;
            // finishing work on these paths also settles older half-edits under them, as the daemon does
            for (auto &old : r.leases)
                if (old.dirty && old.state != "held" && (old.glob == l.glob || old.glob.startsWith(l.glob.chopped(l.glob.endsWith("**") ? 2 : 0))))
                    old.dirty = false;
        }
    } else if (type == "overlap" || type == "landed" || type == "flag" || type == "contend") {
        r.alerts.append(Alert{repo, id, type, ts, text, device});
        if (r.alerts.size() > 200)
            r.alerts.removeFirst();
    }

    if (type == "note" || type == "stop") {
        // a Stop entry with no text says only that the session ended
        if (!(type == "stop" && text.isEmpty()) && session != "sankofad")
            r.handoffs.insert(session, Handoff{repo, session, agent, device, ts, type, text});
    }
    emit changed();
}

QStringList Ledger::repos() const
{
    QStringList list = m_repos.keys();
    std::sort(list.begin(), list.end());
    return list;
}

QList<Ledger::Item> Ledger::openItems(const QString &repo, const QString &type) const
{
    QList<Item> out;
    for (auto it = m_repos.begin(); it != m_repos.end(); ++it) {
        if (!repo.isEmpty() && it.key() != repo)
            continue;
        for (const auto &item : it.value().items)
            if (!item.resolved && (type.isEmpty() || item.type == type))
                out.append(item);
    }
    std::sort(out.begin(), out.end(), [](const Item &a, const Item &b) { return a.ts > b.ts; });
    return out;
}

QList<Ledger::Lease> Ledger::leases(const QString &repo, const QDateTime &now) const
{
    const QString at = now.toUTC().toString("yyyy-MM-ddTHH:mm:ss.zzzZ");
    QList<Lease> out;
    for (auto it = m_repos.begin(); it != m_repos.end(); ++it) {
        if (!repo.isEmpty() && it.key() != repo)
            continue;
        for (Lease l : it.value().leases) {
            // without the daemon nobody writes expire events, so a lease past its time counts as expired
            if (l.state == "held" && !l.expires.isEmpty() && l.expires <= at)
                l.state = "expired";
            if (l.state == "held" || (l.dirty && (l.state == "released" || l.state == "expired")))
                out.append(l);
        }
    }
    std::sort(out.begin(), out.end(), [](const Lease &a, const Lease &b) {
        if ((a.state == "held") != (b.state == "held"))
            return a.state == "held";
        return a.claimed > b.claimed;
    });
    return out;
}

QList<Ledger::Handoff> Ledger::handoffs(const QString &repo, int limit) const
{
    QList<Handoff> out;
    for (auto it = m_repos.begin(); it != m_repos.end(); ++it)
        if (repo.isEmpty() || it.key() == repo)
            out.append(it.value().handoffs.values());
    std::sort(out.begin(), out.end(), [](const Handoff &a, const Handoff &b) { return a.ts > b.ts; });
    return out.mid(0, limit);
}

QList<Ledger::Alert> Ledger::alerts(const QString &repo, int limit) const
{
    QList<Alert> out;
    for (auto it = m_repos.begin(); it != m_repos.end(); ++it)
        if (repo.isEmpty() || it.key() == repo)
            out.append(it.value().alerts);
    std::sort(out.begin(), out.end(), [](const Alert &a, const Alert &b) { return a.ts > b.ts; });
    return out.mid(0, limit);
}

int Ledger::eventCount(const QString &repo) const
{
    int n = 0;
    for (auto it = m_repos.begin(); it != m_repos.end(); ++it)
        if (repo.isEmpty() || it.key() == repo)
            n += it.value().events;
    return n;
}

QString Ledger::lastTs(const QString &repo) const
{
    QString t;
    for (auto it = m_repos.begin(); it != m_repos.end(); ++it)
        if ((repo.isEmpty() || it.key() == repo) && it.value().lastTs > t)
            t = it.value().lastTs;
    return t;
}
