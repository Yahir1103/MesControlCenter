const fp = require('fastify-plugin');
const mysql = require('mysql2/promise');
const createPcRepo = require('../repo/pcRepo');

module.exports = fp(async (app) => {
    const pool = mysql.createPool({
        host: process.env.DB_HOST || 'localhost',
        port: parseInt(process.env.DB_PORT || '3306', 10),
        user: process.env.DB_USER,
        password: process.env.DB_PASSWORD,
        database: process.env.DB_DATABASE,
        waitForConnections: true,
        connectionLimit: 5,
        queueLimit: 0,
        enableKeepAlive: true,
        keepAliveInitialDelay: 0,
        charset: 'utf8mb4_unicode_ci',
    });

    try {
        const conn = await pool.getConnection();
        await conn.query('SELECT 1');
        app.log.info(
            `✅ MySQL conectado a ${process.env.DB_HOST}:${process.env.DB_PORT}/${process.env.DB_DATABASE}`
        );
        conn.release();
    } catch (err) {
        app.log.error(`❌ Error conectando a MySQL: ${err.message}`);
    }

    app.decorate('db', pool);
    app.decorate('pcRepo', createPcRepo(pool));

    app.addHook('onClose', async () => {
        app.log.info('Cerrando conexiones MySQL...');
        await pool.end();
    });
});
