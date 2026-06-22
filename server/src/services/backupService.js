const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const { spawn } = require('child_process');
const { pipeline } = require('stream/promises');
const zlib = require('zlib');
const cron = require('node-cron');

const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/;

function readBool(value, fallback) {
    if (value == null || value === '') return fallback;
    return ['1', 'true', 'yes', 'on'].includes(String(value).trim().toLowerCase());
}

function readInt(value, fallback) {
    const parsed = Number.parseInt(String(value ?? ''), 10);
    return Number.isFinite(parsed) ? parsed : fallback;
}

function sanitizeFilePart(value) {
    return String(value || 'database').replace(/[^a-zA-Z0-9_.-]+/g, '_');
}

function compactError(message) {
    return String(message || 'Backup failed.').replace(/\s+/g, ' ').trim().slice(0, 1024);
}

function getDefaultConfig() {
    const backupTime = TIME_RE.test(process.env.BACKUP_TIME || '')
        ? process.env.BACKUP_TIME
        : '22:00';

    return {
        enabled: readBool(process.env.BACKUP_ENABLED, true),
        backup_time: backupTime,
        retention_days: Math.max(1, readInt(process.env.BACKUP_RETENTION_DAYS, 7)),
        backup_dir: process.env.BACKUP_DIR || './backups',
        timezone: process.env.BACKUP_TIMEZONE || 'America/Mexico_City',
    };
}

function getZonedParts(date, timezone) {
    const formatter = new Intl.DateTimeFormat('en-US', {
        timeZone: timezone,
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hourCycle: 'h23',
    });

    const parts = {};
    for (const part of formatter.formatToParts(date)) {
        if (part.type !== 'literal')
            parts[part.type] = Number(part.value);
    }
    return parts;
}

function addDaysToYmd(parts, days) {
    const date = new Date(Date.UTC(parts.year, parts.month - 1, parts.day + days));
    return {
        year: date.getUTCFullYear(),
        month: date.getUTCMonth() + 1,
        day: date.getUTCDate(),
    };
}

function zonedTimeToUtc(parts, hour, minute, timezone) {
    const desiredUtc = Date.UTC(parts.year, parts.month - 1, parts.day, hour, minute, 0);
    let guess = new Date(desiredUtc);

    for (let i = 0; i < 3; i++) {
        const actual = getZonedParts(guess, timezone);
        const actualAsUtc = Date.UTC(
            actual.year,
            actual.month - 1,
            actual.day,
            actual.hour,
            actual.minute,
            actual.second || 0
        );
        guess = new Date(guess.getTime() + desiredUtc - actualAsUtc);
    }

    return guess;
}

function computeNextRunAt(config, now = new Date()) {
    if (!config.enabled || !TIME_RE.test(config.backup_time))
        return null;

    const [hour, minute] = config.backup_time.split(':').map(Number);
    const timezone = config.timezone || 'America/Mexico_City';
    const current = getZonedParts(now, timezone);
    const currentMinutes = current.hour * 60 + current.minute;
    const targetMinutes = hour * 60 + minute;
    const targetDate = addDaysToYmd(current, currentMinutes < targetMinutes ? 0 : 1);
    return zonedTimeToUtc(targetDate, hour, minute, timezone).toISOString();
}

class BackupService {
    constructor(repo, logger) {
        this.repo = repo;
        this.logger = logger;
        this.task = null;
        this.running = false;
        this.currentConfig = null;
        this.onUpdate = null;
    }

    async init() {
        await this.repo.ensureTables();
        await this.repo.ensureDefaultConfig(getDefaultConfig());
        await this.repo.markStaleRunningAsFailed();
        await this.reloadSchedule();
    }

    stop() {
        if (this.task) {
            this.task.stop();
            this.task = null;
        }
    }

    async reloadSchedule() {
        this.stop();
        const config = await this.repo.getConfig();
        this.currentConfig = config;

        if (!config?.enabled) {
            this.logger.info('Backups disabled.');
            return;
        }

        this.assertTimezone(config.timezone);
        const [hour, minute] = config.backup_time.split(':').map(Number);
        const expression = `${minute} ${hour} * * *`;

        this.task = cron.schedule(
            expression,
            () => {
                this.runBackup('scheduled').catch((err) => {
                    this.logger.error(`Scheduled backup error: ${err.message}`);
                });
            },
            {
                scheduled: true,
                timezone: config.timezone,
            }
        );

        this.logger.info(`Backups scheduled at ${config.backup_time} (${config.timezone}).`);
    }

    async updateConfig(patch) {
        const current = await this.repo.getConfig();
        const next = {
            enabled: patch.enabled == null ? current.enabled : !!patch.enabled,
            backup_time: patch.backup_time || current.backup_time,
            retention_days: patch.retention_days == null ? current.retention_days : Number(patch.retention_days),
            backup_dir: patch.backup_dir || current.backup_dir,
            timezone: patch.timezone || current.timezone,
        };

        this.validateConfig(next);
        const saved = await this.repo.updateConfig(next);
        await this.reloadSchedule();
        await this.emitUpdate();
        return saved;
    }

    validateConfig(config) {
        if (!TIME_RE.test(config.backup_time))
            throw new Error('backup_time must use HH:mm format.');
        if (!Number.isInteger(config.retention_days) || config.retention_days < 1)
            throw new Error('retention_days must be a positive integer.');
        if (!config.backup_dir || !String(config.backup_dir).trim())
            throw new Error('backup_dir is required.');
        this.assertTimezone(config.timezone);
    }

    assertTimezone(timezone) {
        try {
            new Intl.DateTimeFormat('en-US', { timeZone: timezone }).format(new Date());
        } catch {
            throw new Error(`Invalid backup timezone: ${timezone}`);
        }
    }

    async getStatus() {
        const config = await this.repo.getConfig();
        return {
            config,
            is_running: this.running,
            next_run_at: config ? computeNextRunAt(config) : null,
            last_run: await this.repo.getLastRun(),
        };
    }

    async getRuns(limit = 50) {
        return this.repo.getRecentRuns(limit);
    }

    async startManualBackup() {
        if (this.running) {
            const run = await this.repo.insertSkipped('manual', 'A backup is already running.');
            await this.emitUpdate();
            return { started: false, run };
        }

        this.runBackup('manual').catch((err) => {
            this.logger.error(`Manual backup error: ${err.message}`);
        });
        return { started: true };
    }

    async runBackup(runType) {
        if (this.running) {
            const run = await this.repo.insertSkipped(runType, 'A backup is already running.');
            await this.emitUpdate();
            return run;
        }

        this.running = true;
        const started = Date.now();
        let runId = null;

        try {
            const config = await this.repo.getConfig();
            if (!config?.enabled && runType === 'scheduled') {
                const run = await this.repo.insertSkipped(runType, 'Backups are disabled.');
                await this.emitUpdate();
                return run;
            }

            runId = await this.repo.createRun(runType);
            await this.emitUpdate();

            const result = await this.createDump(config);
            const run = await this.repo.finishRun(runId, 'success', {
                ...result,
                duration_ms: Date.now() - started,
            });
            await this.applyRetention(config, result.file_path);
            await this.emitUpdate();
            return run;
        } catch (err) {
            const run = runId == null
                ? await this.repo.insertSkipped(runType, compactError(err.message))
                : await this.repo.finishRun(runId, 'failed', {
                    duration_ms: Date.now() - started,
                    error_message: compactError(err.message),
                });
            await this.emitUpdate();
            return run;
        } finally {
            this.running = false;
            await this.emitUpdate();
        }
    }

    async createDump(config) {
        const dbName = process.env.DB_DATABASE;
        if (!dbName)
            throw new Error('DB_DATABASE is not configured.');

        const backupDir = path.resolve(process.cwd(), config.backup_dir);
        await fsp.mkdir(backupDir, { recursive: true });

        const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
        const safeDb = sanitizeFilePart(dbName);
        const filePath = path.join(backupDir, `${safeDb}_${stamp}.sql.gz`);
        const tempPath = `${filePath}.tmp`;

        const args = [
            `--host=${process.env.DB_HOST || 'localhost'}`,
            `--port=${process.env.DB_PORT || '3306'}`,
            `--user=${process.env.DB_USER || ''}`,
            '--single-transaction',
            '--quick',
            '--routines',
            '--events',
            '--triggers',
            '--databases',
            dbName,
        ];

        const env = { ...process.env };
        if (process.env.DB_PASSWORD)
            env.MYSQL_PWD = process.env.DB_PASSWORD;

        const dump = spawn(process.env.MYSQLDUMP_PATH || 'mysqldump', args, {
            env,
            stdio: ['ignore', 'pipe', 'pipe'],
            windowsHide: true,
        });

        let stderr = '';
        dump.stderr.on('data', (chunk) => {
            stderr += chunk.toString();
            if (stderr.length > 8192)
                stderr = stderr.slice(-8192);
        });

        const exitPromise = new Promise((resolve, reject) => {
            dump.on('error', reject);
            dump.on('close', (code) => {
                if (code === 0) resolve();
                else reject(new Error(stderr || `mysqldump exited with code ${code}`));
            });
        });

        try {
            await Promise.all([
                pipeline(dump.stdout, zlib.createGzip(), fs.createWriteStream(tempPath)),
                exitPromise,
            ]);
            await fsp.rename(tempPath, filePath);
            const stat = await fsp.stat(filePath);
            return { file_path: filePath, file_size_bytes: stat.size };
        } catch (err) {
            await fsp.rm(tempPath, { force: true }).catch(() => {});
            throw err;
        }
    }

    async applyRetention(config, currentFilePath) {
        const retentionMs = Number(config.retention_days) * 24 * 60 * 60 * 1000;
        const cutoff = Date.now() - retentionMs;
        const backupDir = path.resolve(process.cwd(), config.backup_dir);
        const safeDb = sanitizeFilePart(process.env.DB_DATABASE);

        let entries;
        try {
            entries = await fsp.readdir(backupDir, { withFileTypes: true });
        } catch {
            return;
        }

        await Promise.all(entries
            .filter((entry) => entry.isFile()
                && entry.name.startsWith(`${safeDb}_`)
                && entry.name.endsWith('.sql.gz'))
            .map(async (entry) => {
                const fullPath = path.join(backupDir, entry.name);
                if (path.resolve(fullPath) === path.resolve(currentFilePath))
                    return;

                const stat = await fsp.stat(fullPath).catch(() => null);
                if (stat && stat.mtimeMs < cutoff)
                    await fsp.rm(fullPath, { force: true });
            }));
    }

    async emitUpdate() {
        if (typeof this.onUpdate !== 'function')
            return;
        try {
            await this.onUpdate(await this.getStatus());
        } catch (err) {
            this.logger.error(`Backup update emit failed: ${err.message}`);
        }
    }
}

module.exports = BackupService;
