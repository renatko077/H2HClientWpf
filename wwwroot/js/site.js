// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.querySelectorAll(".tab-button").forEach(button => {
    button.addEventListener("click", () => {
        document.querySelectorAll(".tab-button").forEach(item => item.classList.remove("active"));
        document.querySelectorAll(".tab-panel").forEach(item => item.classList.remove("active"));
        button.classList.add("active");
        document.querySelector(`[data-panel="${button.dataset.tab}"]`)?.classList.add("active");
    });
});

const disputeFiles = document.getElementById("disputeFiles");
const fileCount = document.getElementById("fileCount");
disputeFiles?.addEventListener("change", () => {
    const count = disputeFiles.files?.length ?? 0;
    if (count > 5) {
        alert("Можно выбрать не больше 5 фотографий.");
        disputeFiles.value = "";
        fileCount.textContent = "Выбрано: 0/5";
        return;
    }
    fileCount.textContent = `Выбрано: ${count}/5`;
});

document.querySelectorAll("[data-copy]").forEach(button => {
    button.addEventListener("click", async () => {
        const target = document.querySelector(button.dataset.copy);
        if (!target) return;
        await navigator.clipboard.writeText(target.textContent.trim());
        const original = button.textContent;
        button.textContent = "Скопировано";
        setTimeout(() => button.textContent = original, 1200);
    });
});

const randomDigits = length => {
    const bytes = new Uint8Array(length);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, value => value % 10).join("");
};

const luhnCheckDigit = number => {
    let sum = 0;
    let doubleDigit = true;
    for (let index = number.length - 1; index >= 0; index--) {
        let digit = Number(number[index]);
        if (doubleDigit) {
            digit *= 2;
            if (digit > 9) digit -= 9;
        }
        sum += digit;
        doubleDigit = !doubleDigit;
    }
    return String((10 - (sum % 10)) % 10);
};

const generateCardNumber = () => {
    const prefixes = ["424242", "444111", "516875", "537541"];
    const prefix = prefixes[crypto.getRandomValues(new Uint8Array(1))[0] % prefixes.length];
    const body = prefix + randomDigits(15 - prefix.length);
    return body + luhnCheckDigit(body);
};

const base58Encode = bytes => {
    const alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    let value = 0n;
    for (const byte of bytes) value = (value << 8n) + BigInt(byte);
    let result = "";
    while (value > 0n) {
        result = alphabet[Number(value % 58n)] + result;
        value /= 58n;
    }
    for (const byte of bytes) {
        if (byte !== 0) break;
        result = "1" + result;
    }
    return result;
};

const sha256 = async bytes => new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));

const generateTronAddress = async () => {
    const payload = new Uint8Array(21);
    payload[0] = 0x41;
    crypto.getRandomValues(payload.subarray(1));
    const firstHash = await sha256(payload);
    const secondHash = await sha256(firstHash);
    const address = new Uint8Array(25);
    address.set(payload);
    address.set(secondHash.subarray(0, 4), 21);
    return base58Encode(address);
};

const generateOrderId = prefix => {
    const now = new Date();
    const stamp = [now.getUTCFullYear(), now.getUTCMonth() + 1, now.getUTCDate(), now.getUTCHours(), now.getUTCMinutes(), now.getUTCSeconds()]
        .map((part, index) => index === 0 ? String(part) : String(part).padStart(2, "0"))
        .join("");
    return `${prefix}-${stamp}-${randomDigits(4)}`;
};

const setGeneratedValue = (button, value) => {
    const input = button.closest(".input-action")?.querySelector("input");
    if (!input) return;
    input.value = value;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.focus();
};

document.querySelectorAll("[data-autogenerate='card']").forEach(input => {
    if (!input.value) input.value = generateCardNumber();
});

document.querySelectorAll("[data-generate]").forEach(button => {
    button.addEventListener("click", async () => {
        const type = button.dataset.generate;
        if (type === "card") setGeneratedValue(button, generateCardNumber());
        if (type === "order-id") setGeneratedValue(button, generateOrderId(button.dataset.prefix || "ORDER"));
        if (type === "private-key") {
            const bytes = new Uint8Array(32);
            crypto.getRandomValues(bytes);
            setGeneratedValue(button, Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join(""));
        }
        if (type === "tron-address") {
            button.disabled = true;
            try { setGeneratedValue(button, await generateTronAddress()); }
            finally { button.disabled = false; }
        }
    });
});

document.querySelectorAll("[data-fill-latest-session]").forEach(button => {
    button.addEventListener("click", () => {
        const sessionId = button.dataset.fillLatestSession;
        if (sessionId) setGeneratedValue(button, sessionId);
        else alert("Сначала создайте платёж или выплату — Session ID подставится автоматически.");
    });
});

document.addEventListener("click", event => {
    document.querySelectorAll(".header-menu[open]").forEach(menu => {
        if (!menu.contains(event.target)) menu.removeAttribute("open");
    });
});

document.querySelectorAll("[data-close-details]").forEach(button => {
    button.addEventListener("click", () => button.closest("details")?.removeAttribute("open"));
});

document.querySelectorAll("[data-account-login]").forEach(button => {
    button.addEventListener("click", () => {
        const form = button.closest("form");
        const loginInput = form?.querySelector("input[name='Login']");
        const passwordInput = form?.querySelector("input[name='Password']");
        if (!loginInput) return;
        loginInput.value = button.dataset.accountLogin || "";
        if (passwordInput) passwordInput.value = "";
        loginInput.focus();
    });
});

document.querySelectorAll("[data-login-error='true']").forEach(details => details.setAttribute("open", ""));
