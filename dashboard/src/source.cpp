#include "source.h"

#include "ledger.h"

#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <sys/stat.h>

QString LedgerFiles::defaultRegistry()
{
    QString config = qEnvironmentVariable("XDG_CONFIG_HOME");
    if (config.isEmpty())
        config = QDir::homePath() + "/.config";
    return config + "/sankofa/watched";
}

QString LedgerFiles::ledgerOf(const QString &root)
{
    return root.endsWith(".jsonl") ? root : root + "/.sankofa/ledger.jsonl";
}

LedgerFiles::LedgerFiles(Ledger *ledger, const QStringList &repos, const QString &registry, QObject *parent)
    : QObject(parent), m_ledger(ledger), m_fixed(repos),
      m_registry(registry.isEmpty() ? defaultRegistry() : registry)
{
    m_debounce.setSingleShot(true);
    m_debounce.setInterval(50);
    connect(&m_watcher, &QFileSystemWatcher::fileChanged, this, [this] { m_debounce.start(); });
    connect(&m_watcher, &QFileSystemWatcher::directoryChanged, this, [this] { m_debounce.start(); });
    connect(&m_debounce, &QTimer::timeout, this, &LedgerFiles::poll);
    // inotify can miss a replaced file; a slow look now and then catches anything it did
    m_slow.setInterval(5000);
    connect(&m_slow, &QTimer::timeout, this, &LedgerFiles::poll);
    m_slow.start();
    poll();
}

void LedgerFiles::reloadRegistry()
{
    QStringList roots;
    if (!m_fixed.isEmpty()) {
        for (const auto &r : m_fixed)
            roots << (r.endsWith(".jsonl") ? QFileInfo(r).absoluteFilePath() : QDir(r).absolutePath());
    } else {
        QFile f(m_registry);
        if (f.open(QIODevice::ReadOnly))
            for (const auto &line : QString::fromUtf8(f.readAll()).split('\n'))
                if (!line.trimmed().isEmpty())
                    roots << line.trimmed();
    }
    roots.removeDuplicates();
    for (const auto &gone : m_roots)
        if (!roots.contains(gone)) {
            m_pos.remove(gone);
            m_ledger->resetRepo(gone);
        }
    m_roots = roots;
}

void LedgerFiles::poll()
{
    reloadRegistry();
    QStringList missing;
    for (const auto &root : m_roots) {
        if (!QFileInfo::exists(ledgerOf(root)))
            missing << root;
        else
            read(root);
    }
    if (missing != m_missing) {
        m_missing = missing;
        emit changed();
    }
    rearm();
}

void LedgerFiles::read(const QString &root)
{
    const QString path = ledgerOf(root);
    struct stat st {};
    if (stat(QFile::encodeName(path).constData(), &st) != 0)
        return;
    Pos &p = m_pos[root];
    if (qint64(st.st_ino) != p.inode || st.st_size < p.offset) {
        // a new file, or this one was replaced or cut: read it all again
        if (p.inode != -1)
            m_ledger->resetRepo(root);
        p = Pos{0, {}, qint64(st.st_ino)};
    }
    if (st.st_size == p.offset)
        return;
    QFile f(path);
    if (!f.open(QIODevice::ReadOnly) || !f.seek(p.offset))
        return;
    QByteArray data = p.partial + f.readAll();
    p.offset += data.size() - p.partial.size();
    const int end = data.lastIndexOf('\n');
    p.partial = end < 0 ? data : data.mid(end + 1);   // a half-written last line waits for the rest
    if (end < 0)
        return;
    for (const auto &line : data.left(end).split('\n')) {
        if (line.trimmed().isEmpty())
            continue;
        const auto doc = QJsonDocument::fromJson(line);
        if (doc.isObject())
            m_ledger->ingest(root, doc.object());
    }
}

void LedgerFiles::rearm()
{
    QStringList want{QFileInfo(m_registry).absolutePath(), m_registry};
    for (const auto &root : m_roots)
        want << ledgerOf(root) << QFileInfo(ledgerOf(root)).absolutePath();
    QStringList present;
    for (const auto &w : want)
        if (QFileInfo::exists(w))
            present << w;
    const auto now = m_watcher.files() + m_watcher.directories();
    for (const auto &w : now)
        if (!present.contains(w))
            m_watcher.removePath(w);
    for (const auto &w : present)
        if (!now.contains(w))
            m_watcher.addPath(w);
}
