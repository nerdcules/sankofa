#pragma once

#include <QDateTime>
#include <QHash>
#include <QJsonObject>
#include <QList>
#include <QObject>
#include <QString>
#include <QVariantList>

// Folds sankofa ledger events into what a person wants to see at a glance: who holds which
// paths, what is open, what is waiting on them, and the last handoff of each session. It only
// reads; nothing here writes to a ledger or asks the daemon to change anything.
class Ledger : public QObject {
    Q_OBJECT

public:
    explicit Ledger(QObject *parent = nullptr) : QObject(parent) {}

    struct Item {    // a todo, awaiting or handoff entry
        QString repo, id, type, text, ts, session, agent, device;
        bool resolved = false;
        QString resolvedTs, resolution;
    };
    struct Lease {
        QString repo, id, glob, session, agent, device, claimed, expires;
        QString state = "held";   // held, released, done, expired
        bool dirty = true;
    };
    struct Handoff { // the newest note, stop or done of one session
        QString repo, session, agent, device, ts, type, text;
    };
    struct Alert {   // things that happened without anyone asking: overlap, landed, flag, contend
        QString repo, id, type, ts, text, device;
    };

    // One event, as a ledger line (JSON). `repo` names the ledger it came from.
    void ingest(const QString &repo, const QJsonObject &event);
    // A ledger was replaced (a hub replica rewritten): forget what it said before.
    void resetRepo(const QString &repo);

    QStringList repos() const;
    QList<Item> openItems(const QString &repo = {}, const QString &type = {}) const;
    QList<Lease> leases(const QString &repo, const QDateTime &now) const;   // held, and ended ones still dirty
    QList<Handoff> handoffs(const QString &repo = {}, int limit = 30) const;
    QList<Alert> alerts(const QString &repo = {}, int limit = 30) const;
    int eventCount(const QString &repo = {}) const;
    QString lastTs(const QString &repo = {}) const;

signals:
    void changed();

private:
    struct Repo {
        QHash<QString, Item> items;
        QHash<QString, Lease> leases;
        QHash<QString, Handoff> handoffs;   // by session
        QList<Alert> alerts;
        int events = 0;
        QString lastTs;
    };
    QHash<QString, Repo> m_repos;
};
