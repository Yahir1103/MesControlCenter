const fp = require('fastify-plugin');

// Hub de conexiones WS. Mantiene dos registries:
//  - agentsByPcKey: pc_key -> socket (una PC monitoreada por conexión)
//  - dashboards: Set de sockets admin que reciben push de cambios
module.exports = fp(async (app) => {
    const agentsByPcKey = new Map();
    const dashboards = new Set();

    function registerAgent(pcKey, socket) {
        // Si ya había una conexión para esa PC, cerramos la vieja.
        const prev = agentsByPcKey.get(pcKey);
        if (prev && prev !== socket) {
            try { prev.close(); } catch { /* ignore */ }
        }
        agentsByPcKey.set(pcKey, socket);
        app.log.info(`📡 Agente conectado: ${pcKey} (total agentes: ${agentsByPcKey.size})`);
    }

    function unregisterAgent(pcKey, socket) {
        if (agentsByPcKey.get(pcKey) === socket) {
            agentsByPcKey.delete(pcKey);
            app.log.info(`📴 Agente desconectado: ${pcKey} (total agentes: ${agentsByPcKey.size})`);
        }
    }

    function isAgentOnline(pcKey) {
        const s = agentsByPcKey.get(pcKey);
        return !!s && s.readyState === 1;
    }

    function sendToAgent(pcKey, msg) {
        const socket = agentsByPcKey.get(pcKey);
        if (!socket || socket.readyState !== 1) return false;
        try {
            socket.send(JSON.stringify(msg));
            return true;
        } catch (err) {
            app.log.error(`Error enviando a agente ${pcKey}: ${err.message}`);
            return false;
        }
    }

    function registerDashboard(socket) {
        dashboards.add(socket);
        app.log.info(`🖥️  Dashboard conectado (total: ${dashboards.size})`);
    }

    function unregisterDashboard(socket) {
        if (dashboards.delete(socket)) {
            app.log.info(`🖥️  Dashboard desconectado (total: ${dashboards.size})`);
        }
    }

    function broadcastDashboards(msg) {
        const payload = JSON.stringify(msg);
        for (const socket of dashboards) {
            try {
                if (socket.readyState === 1) socket.send(payload);
            } catch (err) {
                app.log.error(`Error broadcast dashboard: ${err.message}`);
            }
        }
    }

    function getStats() {
        return {
            agents: agentsByPcKey.size,
            dashboards: dashboards.size,
            agentKeys: [...agentsByPcKey.keys()],
        };
    }

    app.decorate('hub', {
        registerAgent,
        unregisterAgent,
        isAgentOnline,
        sendToAgent,
        registerDashboard,
        unregisterDashboard,
        broadcastDashboards,
        getStats,
    });
});
