// Provide a minimal grecaptcha stub for Node tests.
// Staging environment does not validate the token, so any string is fine.
const token = process.env.RECAPTCHA_TEST_TOKEN || 'e2e-test-token';

global.grecaptcha = {
  ready: (fn) => fn(),
  execute: () => Promise.resolve(token),
};
