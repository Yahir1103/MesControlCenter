// Smoke test del protocolo WS end-to-end contra un servidor ya corriendo.
// Simula un agente y un dashboard, y verifica el flujo: auth, snapshot,
// heartbeat, comando push y command_result.
//
// Uso: node test/smoke.js [ws://localhost:8092/ws] [ADMIN_TOKEN]
const WebSocket = require('ws');

const URL = process.argv[2] || 'ws://localhost:8092/ws';
const ADMIN_TOKEN = process.argv[3] || process.env.ADMIN_TOKEN || '';

const PC_KEY = 'SMOKE-TEST-PC-' + Date.now();
const API_SECRET = 'a'.repeat(64); // 64 hex chars, como genera el cliente real

const log = (who, ...a) => console.log(`[${who}]`, ...a);
const delay = (ms) => new Promise((r) => setTimeout(r, ms));

function connect(name) {
    return new Promise((resolve, reject) => {
        const ws = new WebSocket(URL);
        ws.on('open', () => resolve(ws));
        ws.on('error', reject);
        ws.on('unexpected-response', (_, res) => reject(new Error('HTTP ' + res.statusCode)));
    });
}

const send = (ws, obj) => ws.send(JSON.stringify(obj));

(async () => {
    let pass = 0, fail = 0;
    const ok = (cond, msg) => { if (cond) { pass++; log('PASS', msg); } else { fail++; log('FAIL', msg); } };

    // ───── Agente ─────
    const agent = await connect('agent');
    const agentMsgs = [];
    agent.on('message', (raw) => {
        const m = JSON.parse(raw.toString());
        agentMsgs.push(m);
        log('agent<-', m.type);
        if (m.type === 'command') {
            // Responde resultado
            send(agent, {
                type: 'command_result', command_id: m.command_id,
                status: 'done', result_msg: 'smoke ok',
            });
        }
    });

    send(agent, { type: 'auth', role: 'agent', pc_key: PC_KEY, api_secret: API_SECRET, pc_name: 'SmokeTest' });
    await delay(800);
    ok(agentMsgs.some((m) => m.type === 'auth_ok'), 'agente recibe auth_ok');

    // Heartbeat + estado de un script
    send(agent, { type: 'heartbeat', active: true });
    send(agent, { type: 'script_status', scripts: [{ name: 'smoke.py', active: true, extra: { pid: 123 } }] });
    await delay(500);

    // ───── Dashboard ─────
    const dash = await connect('dash');
    const dashMsgs = [];
    dash.on('message', (raw) => {
        const m = JSON.parse(raw.toString());
        dashMsgs.push(m);
        log('dash<-', m.type);
    });

    send(dash, { type: 'auth', role: 'dashboard', token: ADMIN_TOKEN });
    await delay(800);
    ok(dashMsgs.some((m) => m.type === 'auth_ok'), 'dashboard recibe auth_ok');
    const snap = dashMsgs.find((m) => m.type === 'pcs_snapshot');
    ok(!!snap, 'dashboard recibe pcs_snapshot');
    ok(snap && snap.pcs.some((p) => p.pc_key === PC_KEY), 'el PC de prueba aparece en el snapshot');

    // Backup status/config messages.
    send(dash, { type: 'get_backup_status' });
    await delay(500);
    const backupStatus = dashMsgs.find((m) => m.type === 'backup_status');
    ok(!!backupStatus, 'dashboard recibe backup_status');
    ok(backupStatus && backupStatus.config && backupStatus.config.backup_time, 'backup_status incluye configuración');

    if (backupStatus && backupStatus.config) {
        send(dash, {
            type: 'update_backup_config',
            config: {
                enabled: backupStatus.config.enabled,
                backup_time: backupStatus.config.backup_time,
                retention_days: backupStatus.config.retention_days,
            },
        });
        await delay(500);
        ok(dashMsgs.some((m) => m.type === 'backup_config_saved'), 'dashboard puede guardar configuración backup');
    }

    send(dash, { type: 'get_backup_runs', limit: 5 });
    await delay(500);
    ok(dashMsgs.some((m) => m.type === 'backup_runs'), 'dashboard recibe backup_runs');

    if (process.env.SMOKE_RUN_BACKUP === '1') {
        send(dash, { type: 'run_backup_now' });
        await delay(1000);
        ok(dashMsgs.some((m) => m.type === 'backup_run_started'), 'dashboard puede disparar backup manual');
    }

    // get_scripts
    send(dash, { type: 'get_scripts', pc_key: PC_KEY });
    await delay(500);
    const scripts = dashMsgs.find((m) => m.type === 'scripts');
    ok(scripts && scripts.scripts.some((s) => s.script_name === 'smoke.py'), 'get_scripts devuelve smoke.py');

    // command RESTART_SCRIPT → debe llegar push al agente
    send(dash, { type: 'command', target_pc_key: PC_KEY, command: 'PING', payload: {} });
    await delay(800);
    ok(agentMsgs.some((m) => m.type === 'command'), 'el agente recibe el comando por push');
    const queued = dashMsgs.find((m) => m.type === 'command_queued');
    ok(queued && queued.delivered === true, 'dashboard ve command_queued delivered=true');

    // Limpieza: borrar el PC de prueba
    send(dash, { type: 'delete_pc', pc_key: PC_KEY });
    await delay(500);
    ok(dashMsgs.some((m) => m.type === 'pc_deleted'), 'delete_pc confirma pc_deleted');

    agent.close();
    dash.close();
    await delay(200);

    console.log(`\n=== RESULT: ${pass} passed, ${fail} failed ===`);
    process.exit(fail === 0 ? 0 : 1);
})().catch((err) => {
    console.error('Smoke test error:', err.message);
    process.exit(2);
});
