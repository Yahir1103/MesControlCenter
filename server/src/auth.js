const crypto = require('crypto');

// SHA-256 hex en minúsculas. Debe coincidir con
// CredentialService.HashApiSecret en el cliente C#.
function hashApiSecret(apiSecret) {
    return crypto.createHash('sha256').update(apiSecret, 'utf8').digest('hex').toLowerCase();
}

// Comparación en tiempo constante para evitar timing attacks.
function safeEquals(a, b) {
    if (typeof a !== 'string' || typeof b !== 'string') return false;
    const ba = Buffer.from(a);
    const bb = Buffer.from(b);
    if (ba.length !== bb.length) return false;
    return crypto.timingSafeEqual(ba, bb);
}

module.exports = { hashApiSecret, safeEquals };
