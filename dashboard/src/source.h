#pragma once

#include <QFileSystemWatcher>
#include <QHash>
#include <QObject>
#include <QStringList>
#include <QTimer>

class Ledger;

// Reads the ledgers of the repos the desktop notifier watches (the same registry,
// $XDG_CONFIG_HOME/sankofa/watched, kept by `sankofa watch add`), or the repos named on the
// command line, and follows them as they grow. Each .sankofa/ledger.jsonl is the daemon's
// mirror of its event log, and the replica a hub keeps current, so this sees every event the
// daemon's own stream would, with or without a daemon. A ledger that shrinks or is replaced
// (a hub replica after it reconciled) is read again from the start.
class LedgerFiles : public QObject {
    Q_OBJECT
    Q_PROPERTY(QStringList missing READ missing NOTIFY changed)

public:
    // repos: repo roots (or ledger files) to follow; empty means the registry.
    LedgerFiles(Ledger *ledger, const QStringList &repos = {}, const QString &registry = {}, QObject *parent = nullptr);

    QStringList roots() const { return m_roots; }
    QStringList missing() const { return m_missing; }
    QString registry() const { return m_registry; }
    static QString ledgerOf(const QString &root);
    static QString defaultRegistry();

    // Read whatever is new everywhere (also run by the watcher and a slow timer).
    void poll();

signals:
    void changed();

private:
    void reloadRegistry();
    void read(const QString &root);
    void rearm();

    struct Pos { qint64 offset = 0; QByteArray partial; qint64 inode = -1; };
    Ledger *m_ledger;
    QStringList m_fixed, m_roots, m_missing;
    QString m_registry;
    QHash<QString, Pos> m_pos;
    QFileSystemWatcher m_watcher;
    QTimer m_debounce, m_slow;
};
