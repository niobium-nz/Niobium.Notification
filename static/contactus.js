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

    async function handleContactFormSubmission() {
      try {
        const success = await contactUs(
          "your-recaptcha-key",
          "your-tenant",
          "John Doe",
          "john.doe@example.com",
          "This is a test message."
        );
        if (success) {
          console.log("Contact form submitted successfully.");
        }
      } catch (error) {
        console.error("An error occurred during form submission. Display an error message to the user.", error);
        // You can also check the specific error type to display a more friendly message.
        if (error instanceof ApiError && error.status === 400) {
          alert("Please check your input and try again.");
        } else {
          alert("An unexpected error occurred. Please try again later.");
        }
      }
    }
 */

/**
 * Custom error class for API failures.
 * This provides a more descriptive error type for consumers.
 */
class ApiError extends Error {
    constructor(message, status = null) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
    }
}

/**
 * Generates a compliant globally unique identifier (GUID).
 * This function does not use jQuery and remains unchanged.
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
            await new Promise(resolve => setTimeout(resolve, delay));
            return await fetchWithRetry(url, options, retries - 1);
        }
        return response;
    } catch (error) {
        if (retries > 0) {
            console.warn('Fetch failed due to network error. Retrying...', error);
            const delay = 1000 * (4 - retries);
            await new Promise(resolve => setTimeout(resolve, delay));
            return await fetchWithRetry(url, options, retries - 1);
        }
        throw error;
    }
}

/**
 * Submits a contact us request after executing reCAPTCHA.
 * @param {string} reCapthchaPublicKey The reCAPTCHA public key.
 * @param {string} tenant The tenant identifier.
 * @param {string} name The contact's name.
 * @param {string} contact The contact information (e.g., email or phone).
 * @param {string} message The message content.
 * @returns {Promise<boolean>} A promise that resolves to true on success, or rejects on error.
 */
async function contactUs(reCapthchaPublicKey, tenant, name, contact, message) {
    try {
        await grecaptcha.ready();
        const token = await grecaptcha.execute(reCapthchaPublicKey, { action: "submit" });

        const data = {
            id: generateGUID(),
            tenant: tenant,
            name: name,
            contact: contact,
            message: message,
            token: token,
        };

        const url = "https://niobiumnotifyfunc.azurewebsites.net/Notification";
        const options = {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(data),
        };

        const response = await fetchWithRetry(url, options);

        if (!response.ok) {
            const errorText = await response.text();
            throw new ApiError(`API request failed: ${response.statusText}. Details: ${errorText}`, response.status);
        }

        return true;

    } catch (error) {
        // Log the error for debugging purposes.
        console.error("Contact form submission failed:", error);
        // Re-throw the error so the consumer can handle it.
        throw error;
    }
}