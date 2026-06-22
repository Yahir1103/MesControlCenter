// Persistencia MySQL para MesControlCenter.
// SQL portado 1:1 desde src/MesControlCenter.Data/MySqlPcMonitorRepository.cs.
// El servidor WS es el ÚNICO proceso que escribe en estas tablas.

const DDL_PCS = `
CREATE TABLE IF NOT EXISTS pcs (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  pc_key        VARCHAR(64) NOT NULL UNIQUE,
  pc_name       VARCHAR(64) NOT NULL,
  role          ENUM('USER','ADMIN') NOT NULL DEFAULT 'USER',
  api_secret    CHAR(64) NOT NULL,
  is_active     TINYINT(1) NOT NULL DEFAULT 0,
  last_seen     DATETIME NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  KEY idx_is_active_last_seen (is_active, last_seen)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`;

const DDL_PC_SCRIPTS = `
CREATE TABLE IF NOT EXISTS pc_scripts (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  pc_id         BIGINT NOT NULL,
  script_name   VARCHAR(96) NOT NULL,
  is_active     TINYINT(1) NOT NULL DEFAULT 0,
  last_heartbeat DATETIME NULL,
  extra_status  JSON NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE KEY u_pc_script (pc_id, script_name),
  KEY idx_is_active (is_active),
  FOREIGN KEY (pc_id) REFERENCES pcs(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`;

const DDL_PC_COMMANDS = `
CREATE TABLE IF NOT EXISTS pc_commands (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  target_pc_id  BIGINT NOT NULL,
  command       ENUM('RESTART_SCRIPT','PING','UPDATE_AGENT') NOT NULL,
  payload       JSON NULL,
  status        ENUM('queued','in_progress','done','failed') NOT NULL DEFAULT 'queued',
  result_msg    VARCHAR(512) NULL,
  created_by    BIGINT NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  KEY k_status_pc (status, target_pc_id, id),
  KEY idx_target_created (target_pc_id, created_at),
  FOREIGN KEY (target_pc_id) REFERENCES pcs(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`;

const DDL_PC_LOGS = `
CREATE TABLE IF NOT EXISTS pc_logs (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  pc_id         BIGINT NULL,
  level         ENUM('INFO','WARN','ERROR') NOT NULL,
  event         VARCHAR(64) NOT NULL,
  message       VARCHAR(1024) NOT NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_pc_created (pc_id, created_at),
  KEY idx_level_created (level, created_at),
  FOREIGN KEY (pc_id) REFERENCES pcs(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`;

module.exports = function createPcRepo(pool) {
    async function ensureTables() {
        await pool.query(DDL_PCS);
        await pool.query(DDL_PC_SCRIPTS);
        await pool.query(DDL_PC_COMMANDS);
        await pool.query(DDL_PC_LOGS);
    }

    // Upsert de PC. Devuelve el id. (Equivale a RegisterPcAsync)
    async function registerPc(pcKey, pcName, apiSecretHash, role = 'USER') {
        await pool.query(
            `INSERT INTO pcs (pc_key, pc_name, role, api_secret, is_active, last_seen)
             VALUES (?, ?, ?, ?, 0, NOW())
             ON DUPLICATE KEY UPDATE pc_name = VALUES(pc_name), updated_at = NOW()`,
            [pcKey, pcName, role, apiSecretHash]
        );
        const [rows] = await pool.query('SELECT id FROM pcs WHERE pc_key = ?', [pcKey]);
        return rows.length ? rows[0].id : null;
    }

    async function getPcByKey(pcKey) {
        const [rows] = await pool.query(
            'SELECT id, pc_key, pc_name, role, api_secret FROM pcs WHERE pc_key = ?',
            [pcKey]
        );
        return rows.length ? rows[0] : null;
    }

    async function updateHeartbeat(pcKey, isActive) {
        const [res] = await pool.query(
            'UPDATE pcs SET is_active = ?, last_seen = NOW() WHERE pc_key = ?',
            [isActive ? 1 : 0, pcKey]
        );
        return res.affectedRows > 0;
    }

    async function updateScriptStatus(pcId, scriptName, isActive, extraStatusJson) {
        await pool.query(
            `INSERT INTO pc_scripts (pc_id, script_name, is_active, last_heartbeat, extra_status)
             VALUES (?, ?, ?, NOW(), ?)
             ON DUPLICATE KEY UPDATE
                is_active = VALUES(is_active),
                last_heartbeat = NOW(),
                extra_status = VALUES(extra_status)`,
            [pcId, scriptName, isActive ? 1 : 0, extraStatusJson ?? null]
        );
        return true;
    }

    async function fetchQueuedCommands(pcId, limit = 20) {
        const [rows] = await pool.query(
            `SELECT id, target_pc_id, command, payload, status, result_msg
             FROM pc_commands
             WHERE target_pc_id = ? AND status = 'queued'
             ORDER BY id ASC
             LIMIT ?`,
            [pcId, limit]
        );
        return rows;
    }

    async function insertCommand(targetPcId, command, payloadJson, createdBy = null) {
        const [res] = await pool.query(
            `INSERT INTO pc_commands (target_pc_id, command, payload, created_by)
             VALUES (?, ?, ?, ?)`,
            [targetPcId, command, payloadJson ?? null, createdBy]
        );
        return res.insertId;
    }

    async function updateCommandStatus(cmdId, status, resultMsg) {
        const msg = resultMsg && resultMsg.length > 512 ? resultMsg.slice(0, 512) : resultMsg;
        await pool.query(
            'UPDATE pc_commands SET status = ?, result_msg = ?, updated_at = NOW() WHERE id = ?',
            [status, msg ?? null, cmdId]
        );
        return true;
    }

    async function getAllPcs() {
        const [rows] = await pool.query(
            `SELECT id, pc_name, pc_key, role, is_active, last_seen,
                    TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS seconds_since_seen, created_at
             FROM pcs
             ORDER BY is_active DESC, last_seen DESC`
        );
        return rows;
    }

    async function getPcScripts(pcId) {
        const [rows] = await pool.query(
            `SELECT id, script_name, is_active, last_heartbeat, extra_status, created_at
             FROM pc_scripts
             WHERE pc_id = ?
             ORDER BY script_name`,
            [pcId]
        );
        return rows;
    }

    async function insertLog(pcId, level, event, message) {
        const msg = message && message.length > 1024 ? message.slice(0, 1024) : message;
        await pool.query(
            'INSERT INTO pc_logs (pc_id, level, event, message) VALUES (?, ?, ?, ?)',
            [pcId ?? null, level, event, msg]
        );
        return true;
    }

    async function deletePc(pcId) {
        const [res] = await pool.query('DELETE FROM pcs WHERE id = ?', [pcId]);
        return res.affectedRows > 0;
    }

    return {
        ensureTables,
        registerPc,
        getPcByKey,
        updateHeartbeat,
        updateScriptStatus,
        fetchQueuedCommands,
        insertCommand,
        updateCommandStatus,
        getAllPcs,
        getPcScripts,
        insertLog,
        deletePc,
    };
};
