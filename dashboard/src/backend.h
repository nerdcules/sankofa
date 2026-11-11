#pragma once

#include <QDateTime>
#include <QObject>
#include <QTimer>
#include <QVariantList>

#include "ledger.h"

class LedgerFiles;

// What the window shows, for one repo or all of them, rebuilt whenever an event arrives.
class Backend : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList repos READ repos NOTIFY changed)
    Q_PROPERTY(int repoIndex READ repoIndex WRITE setRepoIndex NOTIFY changed)
    Q_PROPERTY(QString repoName READ repoName NOTIFY changed)
    Q_PROPERTY(QVariantList waiting READ waiting NOTIFY changed)
    Q_PROPERTY(QVariantList todos READ todos NOTIFY changed)
    Q_PROPERTY(QVariantList leases READ leases NOTIFY changed)
    Q_PROPERTY(QVariantList handoffs READ handoffs NOTIFY changed)
    Q_PROPERTY(QVariantList alerts READ alerts NOTIFY changed)
    Q_PROPERTY(QStringList missing READ missing NOTIFY changed)
    Q_PROPERTY(int events READ events NOTIFY changed)
    Q_PROPERTY(QString lastEvent READ lastEvent NOTIFY changed)
    Q_PROPERTY(bool watching READ watching NOTIFY changed)

public:
    // now: a fixed clock for tests and screenshots; invalid means the real one.
    explicit Backend(Ledger *ledger, LedgerFiles *files = nullptr, const QDateTime &now = {}, QObject *parent = nullptr);

    QVariantList repos() const;
    int repoIndex() const { return m_repoIndex; }
    void setRepoIndex(int i);
    QString repoName() const;
    QVariantList waiting() const { return m_waiting; }
    QVariantList todos() const { return m_todos; }
    QVariantList leases() const { return m_leases; }
    QVariantList handoffs() const { return m_handoffs; }
    QVariantList alerts() const { return m_alerts; }
    QStringList missing() const;
    int events() const;
    QString lastEvent() const;
    bool watching() const;

    Q_INVOKABLE void nextRepo() { setRepoIndex(m_repoIndex + 1 >= int(m_ledger->repos().size()) ? -1 : m_repoIndex + 1); }
    Q_INVOKABLE void previousRepo() { setRepoIndex(m_repoIndex <= -1 ? int(m_ledger->repos().size()) - 1 : m_repoIndex - 1); }
    // Puts text on the clipboard (the command to resolve an item, the path a lease covers).
    Q_INVOKABLE void copy(const QString &text);
    Q_INVOKABLE QString ago(const QString &ts) const;
    Q_INVOKABLE void refresh();

    QDateTime now() const { return m_now.isValid() ? m_now : QDateTime::currentDateTimeUtc(); }
    static QString shortName(const QString &root, const QStringList &all);

signals:
    void changed();
    void copied(const QString &text);

private:
    void rebuild();
    QString current() const;

    Ledger *m_ledger;
    LedgerFiles *m_files;
    QDateTime m_now;
    int m_repoIndex = -1;
    QVariantList m_waiting, m_todos, m_leases, m_handoffs, m_alerts;
    QTimer m_tick, m_coalesce;
};
