import path from 'node:path';
import { describe, it, expect } from 'vitest';

// Load the helper script which attaches to global.niobium.notification
await import(path.resolve(process.cwd(), '../assets/contact-us.js'));
await import(path.resolve(process.cwd(), '../assets/subscribe.js'));

// Ensure Node 18+ provides global fet
if (typeof fetch !== 'function') {
    throw new Error('Global fetch is required (Node 18+).');
}

describe('Notification end-to-end tests', () => {
    it('contact-us.js posts contact data and receives a successful response', async () => {
        const apiUrl = process.env.API_URL;
        if (!apiUrl) {
            throw new Error('API_URL environment variable is required.');
        }

        const recaptchaKey = process.env.RECAPTCHA_KEY || 'test-recaptcha-key';
        const tenant = process.env.TENANT || 'de8b3e50-abf6-4cdd-9269-f2472a1020ad';
        const name = process.env.NAME || 'E2E Tester';
        const contact = process.env.CONTACT || 'tester@example.com';
        const message = process.env.MESSAGE || 'This is an automated E2E test.';

        const response = await globalThis.niobium.notification.contactUs(
            recaptchaKey,
            tenant,
            name,
            contact,
            message,
            apiUrl,
            true
        );

        // allow common success codes; adjust as needed for your API
        expect([200, 201, 202, 204]).toContain(response.status);

        // Try to parse JSON response if present
        const text = await response.text();
        // Some endpoints might return empty content; that's OK
        if (text) {
            try {
                const json = JSON.parse(text);
                expect(json).toBeDefined();
            } catch (_) {
                // Not JSON; ignore
            }
        }
    });

    it('subscribe.js posts contact data and receives a successful response', async () => {
        const apiUrl = process.env.API_URL;
        if (!apiUrl) {
            throw new Error('API_URL environment variable is required.');
        }

        const recaptchaKey = process.env.RECAPTCHA_KEY || 'test-recaptcha-key';
        const tenant = process.env.TENANT || 'de8b3e50-abf6-4cdd-9269-f2472a1020ad';
        const campaign = process.env.CAMPAIGN || 'test';
        const firstName = process.env.FIRSTNAME || 'E2E';
        const lastName = process.env.LASTNAME || 'Tester';
        const email = process.env.EMAIL || 'hcp5he11@gmail.com';
        const track = process.env.TRACK || 'test';

        const response = await globalThis.niobium.notification.subscribe(
            recaptchaKey,
            tenant,
            campaign,
            email,
            firstName,
            lastName,
            track,
            apiUrl,
            true
        );

        // allow common success codes; adjust as needed for your API
        expect([200, 201, 202, 204]).toContain(response.status);

        // Try to parse JSON response if present
        const text = await response.text();
        
        // Some endpoints might return empty content; that's OK
        if (text) {
            try {
                const json = JSON.parse(text);
                expect(json).toBeDefined();
            } catch (_) {
                // Not JSON; ignore
            }
        }
    });
});
