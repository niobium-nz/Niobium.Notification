/**
 * @typedef {Object} SubscribeData
 * @property {string} id
 * @property {string} tenant
 * @property {string} campaign
 * @property {string} email
 * @property {string} firstName
 * @property {string} lastName
 * @property {string} track
 * @property {string} token
 */

/* 
    * Consumer Example 

    function handleSubscriptionFormSubmission() {
      try {
        niobium.notification.subscribe(
          "your-recaptcha-key",
          "your-tenant",
          "1-dollar-deal",
          "john.doe@example.com",
          "John",
          "Doe",
          "facebook-12",
        );
      } catch (error) {
        console.error("An error occurred during form submission. Display an error message to the user.", error);
      } finally {
        // cleanup or final actions
      }
    }
 */

(function (global) {
  "use strict";

  // Create/resolve namespace: niobium.notification
  const niobium = (global.niobium = global.niobium || {});
  const notificationNS = (niobium.notification = niobium.notification || {});

  /**
   * Generates a compliant globally unique identifier (GUID).
   * @returns {string} The generated GUID.
   */
  function generateGUID() {
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, function (c) {
      const r = (Math.random() * 16) | 0;
      const v = c === "x" ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    });
  }

  /**
   * Executes a fetch request with a retry mechanism.
   * @param {string} url The URL to send the request to.
   * @param {RequestInit} options The fetch options.
   * @param {number} retries The maximum number of retry attempts.
   * @returns {Promise<Response>} The fetch response.
   */
  async function fetchWithRetry(url, options, retries = 3) {
    try {
      const response = await fetch(url, options);

        // If the response is not OK and there are retries left, wait and retry.
      if (!response.ok && retries > 0) {
        console.warn(`Fetch failed with status ${response.status}. Retrying...`);
            // Exponential back-off delay.
        const delay = 1000 * (4 - retries);
        await new Promise((resolve) => setTimeout(resolve, delay));
        return await fetchWithRetry(url, options, retries - 1);
      }
      return response;
    } catch (error) {
      if (retries > 0) {
        console.warn("Fetch failed due to network error. Retrying...", error);
        const delay = 1000 * (4 - retries);
        await new Promise((resolve) => setTimeout(resolve, delay));
        return await fetchWithRetry(url, options, retries - 1);
      }
      throw error;
    }
  }

  /**
   * Submits a subscription request after executing reCAPTCHA.
   * @param {string} reCapthchaPublicKey The reCAPTCHA public key.
   * @param {string} tenant The tenant identifier.
   * @param {string} campaign The campaign identifier.
   * @param {string} email The subscription email.
   * @param {string} firstName Optionally the first name.
   * @param {string} lastName Optionally the last name.
   * @param {string} track Optionally the internal track identifier.
   * @param {string} baseUrl The WebAPI base URL.
   */
    function subscribe(reCapthchaPublicKey, tenant, campaign, email, firstName, lastName, track, baseUrl) {
    if (!global.grecaptcha || !global.grecaptcha.ready) {
      console.error("reCAPTCHA is not loaded.");
      return;
    }

    const url = baseUrl || "/api/notification/subscribe";

    global.grecaptcha.ready(function () {
      global.grecaptcha
        .execute(reCapthchaPublicKey, { action: "submit" })
        .then(function (token) {
          /** @type {SubscriptionData} */
          const data = {
            id: generateGUID(),
            tenant: tenant,
            campaign: campaign,
            email: email,
            firstName: firstName,
            lastName: lastName,
            track: track,
            token: token,
          };

          const options = {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(data),
          };

          fetchWithRetry(url, options);
        });
    });
  }

  // Public API
  notificationNS.subscribe = subscribe;
})(typeof window !== "undefined" ? window : globalThis);