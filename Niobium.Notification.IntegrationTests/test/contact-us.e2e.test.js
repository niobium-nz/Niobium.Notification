import path from 'node:path';
import { describe, it, expect } from 'vitest';

// Load the helper script which attaches to global.niobium.notification
await import(path.resolve(process.cwd(), '../assets/contact-us.js'));

// Ensure Node 18+ provides global fet
if (typeof fetch !== 'function') {
  throw new Error('Global fetch is required (Node 18+).');
}

describe('contact-us.js E2E (vitest)', () => {
  it('posts contact data to the WebAPI and receives a successful response', async () => {
    const apiUrl = process.env.API_URL;
    if (!apiUrl) {
      throw new Error('API_URL environment variable is required.');
    }

    const recaptchaKey = process.env.RECAPTCHA_KEY || 'test-recaptcha-key';
    const tenant = process.env.TENANT || 'test-tenant';
    const name = process.env.NAME || 'E2E Tester';
    const contact = process.env.CONTACT || 'tester@example.com';
    const message = process.env.MESSAGE || 'This is an automated E2E test.';

    const response = await globalThis.niobium.notification.contactUs(
      recaptchaKey,
      tenant,
      name,
      contact,
      message,
      apiUrl
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
