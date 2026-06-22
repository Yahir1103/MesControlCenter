const DDL_BACKUP_CONFIG = `
CREATE TABLE IF NOT EXISTS db_backup_config (
  id              TINYINT PRIMARY KEY,
  enabled         TINYINT(1) NOT NULL DEFAULT 1,
  backup_time     CHAR(5) NOT NULL DEFAULT '22:00',
  retention_days  INT NOT NULL DEFAULT 7,
  backup_dir      VARCHAR(512) NOT NULL,
  timezone        VARCHAR(64) NOT NULL DEFAULT 'America/Mexico_City',
  created_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`;

const DDL_BACKUP_RUNS = `
CREATE TABLE IF NOT EXISTS db_backup_runs (
  id               BIGINT AUTO_INCREMENT PRIMARY KEY,
  run_type         ENUM('scheduled','manual') NOT NULL,
  status           ENUM('running','success','failed','skipped') NOT NULL,
  started_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  finished_at      DATETIME NULL,
  duration_ms      INT NULL,
  file_path        VARCHAR(1024) NULL,
  file_size_bytes  BIGINT NULL,
  error_message    VARCHAR(1024) NULL,
  KEY idx_status_started (status, started_at),
  KEY idx_started (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`;

function normalizeConfig(row) {
    if (!row) return null;
    return {
        enabled: row.enabled === true || row.enabled === 1,
        backup_time: row.backup_time || '22:00',
        retention_days: Number(row.retention_days || 7),
        backup_dir: row.backup_dir || './backups',
        timezone: row.timezone || 'America/Mexico_City',
        updated_at: row.updated_at ?? null,
    };
}

function normalizeRun(row) {
    if (!row) return null;
    return {
        id: row.id,
        run_type: row.run_type,
        status: row.status,
        started_at: row.started_at ?? null,
        finished_at: row.finished_at ?? null,
        duration_ms: row.duration_ms ?? null,
        file_path: row.file_path ?? null,
        file_size_bytes: row.file_size_bytes ?? null,
        error_message: row.error_message ?? null,
    };
}

module.exports = function createBackupRepo(pool) {
    async function ensureTables() {
        await pool.query(DDL_BACKUP_CONFIG);
        await pool.query(DDL_BACKUP_RUNS);
    }

    async function ensureDefaultConfig(defaultConfig) {
        await pool.query(
            `INSERT IGNORE INTO db_backup_config
             (id, enabled, backup_time, retention_days, backup_dir, timezone)
             VALUES (1, ?, ?, ?, ?, ?)`,
            [
                defaultConfig.enabled ? 1 : 0,
                defaultConfig.backup_time,
                defaultConfig.retention_days,
                defaultConfig.backup_dir,
                defaultConfig.timezone,
            ]
        );
    }

    async function getConfig() {
        const [rows] = await pool.query(
            `SELECT enabled, backup_time, retention_days, backup_dir, timezone, updated_at
             FROM db_backup_config
             WHERE id = 1`
        );
        return normalizeConfig(rows[0]);
    }

    async function updateConfig(config) {
        await pool.query(
            `UPDATE db_backup_config
             SET enabled = ?, backup_time = ?, retention_days = ?, backup_dir = ?, timezone = ?
             WHERE id = 1`,
            [
                config.enabled ? 1 : 0,
                config.backup_time,
                config.retention_days,
                config.backup_dir,
                config.timezone,
            ]
        );
        return getConfig();
    }

    async function createRun(runType) {
        const [res] = await pool.query(
            `INSERT INTO db_backup_runs (run_type, status, started_at)
             VALUES (?, 'running', NOW())`,
            [runType]
        );
        return res.insertId;
    }

    async function finishRun(runId, status, result) {
        const errorMessage = result.error_message && result.error_message.length > 1024
            ? result.error_message.slice(0, 1024)
            : result.error_message;

        await pool.query(
            `UPDATE db_backup_runs
             SET status = ?,
                 finished_at = NOW(),
                 duration_ms = ?,
                 file_path = ?,
                 file_size_bytes = ?,
                 error_message = ?
             WHERE id = ?`,
            [
                status,
                result.duration_ms ?? null,
                result.file_path ?? null,
                result.file_size_bytes ?? null,
                errorMessage ?? null,
                runId,
            ]
        );
        return getRun(runId);
    }

    async function insertSkipped(runType, message) {
        const msg = message && message.length > 1024 ? message.slice(0, 1024) : message;
        const [res] = await pool.query(
            `INSERT INTO db_backup_runs
             (run_type, status, started_at, finished_at, duration_ms, error_message)
             VALUES (?, 'skipped', NOW(), NOW(), 0, ?)`,
            [runType, msg ?? null]
        );
        return getRun(res.insertId);
    }

    async function getRun(runId) {
        const [rows] = await pool.query(
            `SELECT id, run_type, status, started_at, finished_at, duration_ms,
                    file_path, file_size_bytes, error_message
             FROM db_backup_runs
             WHERE id = ?`,
            [runId]
        );
        return normalizeRun(rows[0]);
    }

    async function getLastRun() {
        const [rows] = await pool.query(
            `SELECT id, run_type, status, started_at, finished_at, duration_ms,
                    file_path, file_size_bytes, error_message
             FROM db_backup_runs
             ORDER BY started_at DESC, id DESC
             LIMIT 1`
        );
        return normalizeRun(rows[0]);
    }

    async function getRecentRuns(limit = 50) {
        const capped = Math.max(1, Math.min(Number(limit) || 50, 200));
        const [rows] = await pool.query(
            `SELECT id, run_type, status, started_at, finished_at, duration_ms,
                    file_path, file_size_bytes, error_message
             FROM db_backup_runs
             ORDER BY started_at DESC, id DESC
             LIMIT ?`,
            [capped]
        );
        return rows.map(normalizeRun);
    }

    async function markStaleRunningAsFailed() {
        await pool.query(
            `UPDATE db_backup_runs
             SET status = 'failed',
                 finished_at = NOW(),
                 error_message = 'Server restarted before backup finished.'
             WHERE status = 'running'`
        );
    }

    return {
        ensureTables,
        ensureDefaultConfig,
        getConfig,
        updateConfig,
        createRun,
        finishRun,
        insertSkipped,
        getLastRun,
        getRecentRuns,
        markStaleRunningAsFailed,
    };
};
