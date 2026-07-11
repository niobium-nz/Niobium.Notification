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

    const RECAPTCHA_SCRIPT_BASE_URL = "https://www.google.com/recaptcha/api.js";
    const RECAPTCHA_LOAD_TIMEOUT_MS = 10000;
    const RECAPTCHA_READY_TIMEOUT_MS = 10000;
    let reCaptchaScriptLoadPromise = null;
    let reCaptchaScriptLoadSiteKey = null;

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
     * Reads the reCAPTCHA site key from the contact-us.js query string.
     * @returns {string}
     */
    function getConfiguredSiteKey() {
        if (typeof document === "undefined" || !document.currentScript || !document.currentScript.src) {
            return "";
        }

        try {
            const scriptUrl = document.currentScript.src;
            const urlParams = new URLSearchParams(new URL(scriptUrl).search);
            return (urlParams.get("siteKey") || "").trim();
        } catch (error) {
            return "";
        }
    }

    /**
     * Wraps a promise with a timeout.
     * @template T
     * @param {Promise<T>} promise The promise to wrap.
     * @param {number} timeoutMs The timeout in milliseconds.
     * @param {string} message The timeout error message.
     * @returns {Promise<T>}
     */
    function withTimeout(promise, timeoutMs, message) {
        return new Promise((resolve, reject) => {
            const timeoutId = setTimeout(() => reject(new Error(message)), timeoutMs);

            promise.then(
                (value) => {
                    clearTimeout(timeoutId);
                    resolve(value);
                },
                (error) => {
                    clearTimeout(timeoutId);
                    reject(error);
                }
            );
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
     * Ensures the Google reCAPTCHA v3 script is loaded.
     * @param {string} siteKey Your reCAPTCHA site key.
     * @returns {Promise<void>}
     */
    function ensureRecaptchaScript(siteKey) {
        if (typeof global.grecaptcha !== "undefined" && global.grecaptcha.ready) {
            return Promise.resolve();
        }

        if (!siteKey) {
            return Promise.reject(new Error("A reCAPTCHA site key is required to load the reCAPTCHA script."));
        }

        if (reCaptchaScriptLoadPromise) {
            if (reCaptchaScriptLoadSiteKey && reCaptchaScriptLoadSiteKey !== siteKey) {
                return Promise.reject(new Error("A different reCAPTCHA site key is already being used on this page."));
            }

            return reCaptchaScriptLoadPromise;
        }

        reCaptchaScriptLoadSiteKey = siteKey;

        reCaptchaScriptLoadPromise = withTimeout(new Promise((resolve, reject) => {
            if (typeof document === "undefined") {
                reject(new Error("Document is unavailable to load the reCAPTCHA script."));
                return;
            }

            const scriptUrl = `${RECAPTCHA_SCRIPT_BASE_URL}?render=${encodeURIComponent(siteKey)}`;
            const existingScript = document.querySelector(`script[src="${scriptUrl}"]`);

            if (existingScript) {
                if (typeof global.grecaptcha !== "undefined" && global.grecaptcha.ready) {
                    resolve();
                    return;
                }

                existingScript.addEventListener("load", () => resolve(), { once: true });
                existingScript.addEventListener("error", () => reject(new Error("Failed to load the reCAPTCHA script.")), { once: true });
                return;
            }

            const script = document.createElement("script");
            script.src = scriptUrl;
            script.async = true;
            script.defer = true;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error("Failed to load the reCAPTCHA script."));
            document.head.appendChild(script);
        }), RECAPTCHA_LOAD_TIMEOUT_MS, "Timed out while loading the reCAPTCHA script.").catch((error) => {
            reCaptchaScriptLoadPromise = null;
            reCaptchaScriptLoadSiteKey = null;
            throw error;
        });

        return reCaptchaScriptLoadPromise;
    }

    /**
     * Wraps grecaptcha.ready() in a Promise.
     * @returns {Promise<void>}
     */
    function reCaptchaReady() {
        return withTimeout(new Promise((resolve) => {
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
        }), RECAPTCHA_READY_TIMEOUT_MS, "Timed out while waiting for reCAPTCHA to become ready.");
    }

    /**
     * Generates a reCAPTCHA v3 token using async/await.
     * @param {string} siteKey - Your reCAPTCHA site key.
     * @param {string} action - The action name for this request.
     * @returns {Promise<string>} The reCAPTCHA token.
     */
    async function getRecaptchaToken(siteKey, action) {
        if (siteKey) {
            await ensureRecaptchaScript(siteKey);
        }
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
        const resolvedSiteKey = (reCapthchaPublicKey || "").trim() || configuredSiteKey;

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
                if (localTest) {
                    token = "local-test";
                } else {
                    token = await getRecaptchaToken(resolvedSiteKey, "contactUs");
                }
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

    const configuredSiteKey = getConfiguredSiteKey();
    if (configuredSiteKey) {
        ensureRecaptchaScript(configuredSiteKey).catch(() => {
            reCaptchaScriptLoadPromise = null;
        });
    }

    // Public API
    notificationNS.contactUs = contactUs;
})(typeof window !== "undefined" ? window : globalThis);