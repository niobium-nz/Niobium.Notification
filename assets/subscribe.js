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
     * Wraps grecaptcha.ready() in a Promise.
     * @returns {Promise<void>}
     */
    function reCaptchaReady() {
        return new Promise(resolve => {
            // Check if grecaptcha is already defined to handle cases where
            // the library loads before this function is called.
            if (typeof global.grecaptcha !== 'undefined' && global.grecaptcha.ready) {
                global.grecaptcha.ready(resolve);
            } else {
                // Set a timeout to check for grecaptcha.ready() in case the script
                // loads after this function is first invoked.
                const interval = setInterval(() => {
                    if (typeof global.grecaptcha !== 'undefined' && global.grecaptcha.ready) {
                        clearInterval(interval);
                        global.grecaptcha.ready(resolve);
                    }
                }, 50); // Check every 50ms
            }
        });
    }

    /**
     * Generates a reCAPTCHA v3 token using async/await.
     * @param {string} siteKey - Your reCAPTCHA site key.
     * @param {string} action - The action name for this request.
     * @returns {Promise<string>} The reCAPTCHA token.
     */
    async function getRecaptchaToken(siteKey, action) {
        await reCaptchaReady();
        const token = await global.grecaptcha.execute(siteKey, { action: action });
        return token;
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
     * @param {string} baseUrl The WebAPI URL.
     * @param {boolean} localTest Whether testing on local.
     * @returns {Promise<Response>} The fetch response promise.
     */
    async function subscribe(reCapthchaPublicKey, tenant, campaign, email, firstName, lastName, track, baseUrl, localTest = false) {
        let token;
        try {
             token = await getRecaptchaToken(reCapthchaPublicKey, "subscribe");
        } catch (error) {
            return Promise.reject(new Error("reCAPTCHA execution failed."));
        }

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

        const headers = { "Content-Type": "application/json" };
        if (localTest) {
            // For local testing with tools like ngrok that require a Referer header
            headers["Referer"] = "http://127.0.0.1:3000/";
        }

        const options = {
            method: "POST",
            headers: headers,
            body: JSON.stringify(data),
        };

        const url = (baseUrl || "/api/notification") + "/Subscribe";
        return await fetchWithRetry(url, options);
    }

    // Public API
    notificationNS.subscribe = subscribe;
})(typeof window !== "undefined" ? window : globalThis);