const { hashApiSecret, safeEquals } = require('../auth');

// Endpoint WebSocket único para agentes y dashboards.
// El primer mensaje DEBE ser { type: "auth", ... }; sin auth válida se cierra.
module.exports = async function (app) {
    await app.register(require('@fastify/websocket'));

    app.get('/ws', { websocket: true }, (connection /* SocketStream */, req) => {
        const socket = connection.socket;

        // Estado por conexión
        let authed = false;
        let role = null;        // 'agent' | 'dashboard'
        let pcKey = null;
        let pcId = null;

        const send = (obj) => {
            try {
                if (socket.readyState === 1) socket.send(JSON.stringify(obj));
            } catch (err) {
                app.log.error(`send error: ${err.message}`);
            }
        };

        socket.on('message', async (raw) => {
            let msg;
            try {
                msg = JSON.parse(raw.toString());
            } catch {
                send({ type: 'error', error: 'invalid_json' });
                return;
            }

            try {
                if (!authed) {
                    await handleAuth(msg);
                    return;
                }
                if (role === 'agent') {
                    await handleAgentMessage(msg);
                } else if (role === 'dashboard') {
                    await handleDashboardMessage(msg);
                }
            } catch (err) {
                app.log.error(`Error procesando mensaje (${role}): ${err.message}`);
                send({ type: 'error', error: err.message });
            }
        });

        socket.on('close', () => {
            if (role === 'agent' && pcKey) {
                app.hub.unregisterAgent(pcKey, socket);
                // Marcar inactiva al desconectarse y avisar a dashboards.
                app.pcRepo.updateHeartbeat(pcKey, false).catch(() => {});
                app.hub.broadcastDashboards({ type: 'pc_update', pc_key: pcKey, is_active: false });
            } else if (role === 'dashboard') {
                app.hub.unregisterDashboard(socket);
            }
        });

        socket.on('error', (err) => {
            app.log.error(`WS error (${role || 'pre-auth'}): ${err.message}`);
        });

        // ─────────────── Auth ───────────────
        async function handleAuth(msg) {
            if (msg.type !== 'auth') {
                send({ type: 'auth_error', error: 'auth_required' });
                socket.close();
                return;
            }

            if (msg.role === 'agent') {
                if (!msg.pc_key || !msg.api_secret) {
                    send({ type: 'auth_error', error: 'missing_credentials' });
                    socket.close();
                    return;
                }

                const incomingHash = hashApiSecret(msg.api_secret);
                const existing = await app.pcRepo.getPcByKey(msg.pc_key);

                if (existing) {
                    // PC conocida: el hash debe coincidir.
                    if (!safeEquals(existing.api_secret, incomingHash)) {
                        send({ type: 'auth_error', error: 'bad_secret' });
                        socket.close();
                        return;
                    }
                    pcId = existing.id;
                    // Actualiza nombre por si cambió.
                    await app.pcRepo.registerPc(msg.pc_key, msg.pc_name || existing.pc_name, incomingHash, existing.role);
                } else {
                    // Primer registro (TOFU): se guarda el hash de su secreto.
                    pcId = await app.pcRepo.registerPc(msg.pc_key, msg.pc_name || msg.pc_key, incomingHash, 'USER');
                }

                authed = true;
                role = 'agent';
                pcKey = msg.pc_key;
                app.hub.registerAgent(pcKey, socket);
                await app.pcRepo.insertLog(pcId, 'INFO', 'REGISTERED', `PC ${msg.pc_name || pcKey} conectada (WS)`);
                send({ type: 'auth_ok', pc_id: pcId });

                // Entregar comandos en cola que llegaron mientras estaba offline.
                const queued = await app.pcRepo.fetchQueuedCommands(pcId, 50);
                for (const cmd of queued) {
                    send({ type: 'command', command_id: cmd.id, command: cmd.command, payload: cmd.payload });
                }
                return;
            }

            if (msg.role === 'dashboard') {
                const adminToken = process.env.ADMIN_TOKEN || '';
                if (!adminToken || !safeEquals(adminToken, msg.token || '')) {
                    send({ type: 'auth_error', error: 'bad_token' });
                    socket.close();
                    return;
                }
                authed = true;
                role = 'dashboard';
                app.hub.registerDashboard(socket);
                send({ type: 'auth_ok' });

                // Snapshot inicial.
                const pcs = await app.pcRepo.getAllPcs();
                send({ type: 'pcs_snapshot', pcs });
                return;
            }

            send({ type: 'auth_error', error: 'unknown_role' });
            socket.close();
        }

        // ─────────────── Mensajes de agente ───────────────
        async function handleAgentMessage(msg) {
            switch (msg.type) {
                case 'heartbeat': {
                    await app.pcRepo.updateHeartbeat(pcKey, !!msg.active);
                    app.hub.broadcastDashboards({
                        type: 'pc_update', pc_key: pcKey, is_active: !!msg.active, last_seen: new Date().toISOString(),
                    });
                    break;
                }
                case 'script_status': {
                    const scripts = Array.isArray(msg.scripts) ? msg.scripts : [];
                    for (const s of scripts) {
                        const extra = s.extra ? JSON.stringify(s.extra) : null;
                        await app.pcRepo.updateScriptStatus(pcId, s.name, !!s.active, extra);
                        app.hub.broadcastDashboards({
                            type: 'script_update', pc_key: pcKey, script_name: s.name,
                            is_active: !!s.active, extra: s.extra ?? null,
                        });
                    }
                    break;
                }
                case 'command_result': {
                    const status = msg.status === true || msg.status === 'done' ? 'done'
                        : msg.status === false || msg.status === 'failed' ? 'failed'
                        : String(msg.status);
                    await app.pcRepo.updateCommandStatus(msg.command_id, status, msg.result_msg || '');
                    await app.pcRepo.insertLog(
                        pcId, status === 'done' ? 'INFO' : 'ERROR',
                        status === 'done' ? 'CMD_DONE' : 'CMD_FAILED',
                        `Command ${msg.command_id}: ${msg.result_msg || ''}`
                    );
                    break;
                }
                default:
                    send({ type: 'error', error: `unknown_agent_message: ${msg.type}` });
            }
        }

        // ─────────────── Mensajes de dashboard ───────────────
        async function handleDashboardMessage(msg) {
            switch (msg.type) {
                case 'get_pcs': {
                    const pcs = await app.pcRepo.getAllPcs();
                    send({ type: 'pcs_snapshot', pcs });
                    break;
                }
                case 'get_scripts': {
                    const pc = await app.pcRepo.getPcByKey(msg.pc_key);
                    if (!pc) { send({ type: 'error', error: 'pc_not_found' }); break; }
                    const scripts = await app.pcRepo.getPcScripts(pc.id);
                    send({ type: 'scripts', pc_key: msg.pc_key, scripts });
                    break;
                }
                case 'command': {
                    // { command, target_pc_key, payload }
                    const pc = await app.pcRepo.getPcByKey(msg.target_pc_key);
                    if (!pc) { send({ type: 'error', error: 'pc_not_found' }); break; }
                    const payloadJson = msg.payload ? JSON.stringify(msg.payload) : null;
                    const cmdId = await app.pcRepo.insertCommand(pc.id, msg.command, payloadJson);

                    // Push inmediato si el agente está online; si no, queda 'queued'.
                    const delivered = app.hub.sendToAgent(msg.target_pc_key, {
                        type: 'command', command_id: cmdId, command: msg.command, payload: payloadJson,
                    });
                    send({ type: 'command_queued', command_id: cmdId, delivered });
                    break;
                }
                case 'delete_pc': {
                    const pc = await app.pcRepo.getPcByKey(msg.pc_key);
                    if (!pc) { send({ type: 'error', error: 'pc_not_found' }); break; }
                    await app.pcRepo.deletePc(pc.id);
                    app.hub.broadcastDashboards({ type: 'pc_deleted', pc_key: msg.pc_key });
                    send({ type: 'pc_deleted', pc_key: msg.pc_key });
                    break;
                }
                case 'get_backup_status': {
                    send({ type: 'backup_status', ...(await app.backupService.getStatus()) });
                    break;
                }
                case 'get_backup_runs': {
                    const limit = Number.isInteger(msg.limit) ? msg.limit : 50;
                    send({ type: 'backup_runs', runs: await app.backupService.getRuns(limit) });
                    break;
                }
                case 'update_backup_config': {
                    const config = await app.backupService.updateConfig(msg.config || {});
                    send({ type: 'backup_config_saved', config });
                    break;
                }
                case 'run_backup_now': {
                    const result = await app.backupService.startManualBackup();
                    send({ type: 'backup_run_started', ...result });
                    break;
                }
                default:
                    send({ type: 'error', error: `unknown_dashboard_message: ${msg.type}` });
            }
        }
    });
};
