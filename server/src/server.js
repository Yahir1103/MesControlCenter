require('dotenv').config();
const buildApp = require('./app');

const PORT = parseInt(process.env.PORT || '8092', 10);

(async () => {
    const app = await buildApp();

    try {
        await app.listen({ port: PORT, host: '0.0.0.0' });
        app.log.info(`🚀 MESCC WS server escuchando en :${PORT} (endpoint /ws)`);
    } catch (err) {
        app.log.error(err);
        process.exit(1);
    }

    const shutdown = async (signal) => {
        app.log.info(`Recibido ${signal}, cerrando...`);
        try {
            await app.close();
        } finally {
            process.exit(0);
        }
    };
    process.on('SIGINT', () => shutdown('SIGINT'));
    process.on('SIGTERM', () => shutdown('SIGTERM'));
})();
