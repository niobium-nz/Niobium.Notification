/**
 * @typedef {Object} ContactData
 * @property {string} id
 * @property {string} tenant
 * @property {string} name
 * @property {string} contact
 * @property {string} message
 * @property {string} token
 */

/* 
    * Consumer Example 

    function handleContactFormSubmission() {
      try {
        niobium.notification.contactUs(
          "your-recaptcha-key",
          "your-tenant",
          "John Doe",
          "john.doe@example.com",
          "This is a test message."
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
     * Allows passing a function that will be invoked on each attempt to produce fresh options (e.g., for reCAPTCHA tokens).
     * @param {string} url The URL to send the request to.
     * @param {RequestInit|(() => Promise<RequestInit>|RequestInit)} options The fetch options or a factory returning options per attempt.
     * @param {number} retries The maximum number of retry attempts.
     * @returns {Promise<Response>} The fetch response.
     */
    async function fetchWithRetry(url, options, retries = 3) {
        const resolveOptions = async () => (typeof options === "function" ? await /** @type {any} */ (options)() : options);
        try {
            const currentOptions = await resolveOptions();
            const response = await fetch(url, currentOptions);

            // If the response is not OK and there are retries left, wait and retry.
            if (!response.ok && retries > 0) {
                console.warn(`Fetch failed with status ${response.status}. Retrying...`);
                // Back-off delay.
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
     * Submits a contact us request after executing reCAPTCHA.
     * Ensures a fresh reCAPTCHA token is generated for every retry attempt.
     * @param {string} reCapthchaPublicKey The reCAPTCHA public key.
     * @param {string} tenant The tenant identifier.
     * @param {string} name The contact's name.
     * @param {string} contact The contact information (e.g., email or phone).
     * @param {string} message The message content.
     * @param {string} baseUrl The WebAPI URL.
     * @param {boolean} localTest Whether testing on local.
     * @returns {Promise<Response>} The fetch response promise.
     */
    async function contactUs(reCapthchaPublicKey, tenant, name, contact, message, baseUrl, localTest = false) {
        // Keep request identity and payload stable across retries except for token
        const stableId = generateGUID();

        const headers = { "Content-Type": "application/json" };
        if (localTest) {
            // For local testing with tools like ngrok that require a Referer header
            headers["Referer"] = "http://127.0.0.1:3000/";
        }

        /**
         * Build fresh RequestInit with a new reCAPTCHA token on every attempt
         * @returns {Promise<RequestInit>}
         */
        const buildOptions = async () => {
            let token;
            try {
                token = await getRecaptchaToken(reCapthchaPublicKey, "contactUs");
            } catch (error) {
                return Promise.reject(new Error("reCAPTCHA execution failed."));
            }

            /** @type {ContactData} */
            const data = {
                id: stableId,
                tenant: tenant,
                name: name,
                contact: contact,
                message: message,
                token: token,
            };

            return {
                method: "POST",
                headers: headers,
                body: JSON.stringify(data),
            };
        };

        const url = (baseUrl || "/api/notification") + "/ContactUs";
        return await fetchWithRetry(url, buildOptions);
    }

    // Public API
    notificationNS.contactUs = contactUs;
})(typeof window !== "undefined" ? window : globalThis);