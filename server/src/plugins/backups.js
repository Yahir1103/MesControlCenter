const fp = require('fastify-plugin');
const createBackupRepo = require('../repo/backupRepo');
const BackupService = require('../services/backupService');

module.exports = fp(async (app) => {
    const repo = createBackupRepo(app.db);
    const service = new BackupService(repo, app.log);

    service.onUpdate = (status) => {
        if (app.hub)
            app.hub.broadcastDashboards({ type: 'backup_update', status });
    };

    app.decorate('backupRepo', repo);
    app.decorate('backupService', service);

    app.addHook('onClose', async () => {
        service.stop();
    });
});
