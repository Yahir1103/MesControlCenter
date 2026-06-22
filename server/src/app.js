const Fastify = require('fastify');

module.exports = async function buildApp() {
    const app = Fastify({
        logger: true
    });

    // Plugins
    await app.register(require('./plugins/mysql'));
    await app.register(require('./plugins/wsHub'));
    await app.register(require('./plugins/backups'));

    // Asegura el esquema (mismo DDL que usaba el cliente C#)
    try {
        await app.pcRepo.ensureTables();
        app.log.info('✅ Tablas verificadas/creadas');
    } catch (err) {
        app.log.error(`❌ ensureTables falló: ${err.message}`);
    }

    try {
        await app.backupService.init();
        app.log.info('✅ Backups verificados/programados');
    } catch (err) {
        app.log.error(`❌ backupService init falló: ${err.message}`);
    }

    // Rutas
    await app.register(require('./routes/ws.routes'));

    // Healthcheck simple
    app.get('/health', async () => ({ ok: true, ...app.hub.getStats() }));

    app.setErrorHandler((error, request, reply) => {
        app.log.error(error);
        reply.status(error.statusCode || 500).send({
            ok: false,
            error: error.message || 'Error interno del servidor'
        });
    });

    return app;
};
