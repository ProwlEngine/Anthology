// Browser side of the sample host: the animation frame loop, canvas sizing, image decoding, and the
// status line. Everything here is what Samples/Shared gets from SDL and Magick.NET on the desktop.

let running = false;
let lastTime = 0;

// -- frame loop ---------------------------------------------------------------

export function startLoop(onFrame) {
    if (running) return;
    running = true;
    lastTime = performance.now();

    const tick = (now) => {
        if (!running) return;

        // Seconds, matching the delta the desktop samples get from Silk.NET's Render event.
        const dt = (now - lastTime) / 1000;
        lastTime = now;

        try {
            onFrame(dt);
        } catch (e) {
            running = false;
            reportError(e && e.message ? e.message : String(e));
            throw e;
        }

        requestAnimationFrame(tick);
    };

    requestAnimationFrame(tick);
}

export function stopLoop() {
    running = false;
}

// -- canvas -------------------------------------------------------------------

// The drawing buffer is sized in device pixels while CSS sizes the element, so a high-DPI display
// gets a sharp image instead of an upscaled one. Returns whether the size changed.
export function syncCanvasSize(selector) {
    const canvas = document.querySelector(selector);
    if (!canvas) return false;

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    const height = Math.max(1, Math.round(canvas.clientHeight * dpr));

    if (canvas.width === width && canvas.height === height) return false;

    canvas.width = width;
    canvas.height = height;
    return true;
}

export function canvasWidth(selector) {
    return document.querySelector(selector)?.width ?? 0;
}

export function canvasHeight(selector) {
    return document.querySelector(selector)?.height ?? 0;
}

// -- images -------------------------------------------------------------------

// Decodes an image to tightly packed RGBA bytes. The browser owns every codec the desktop samples
// were using Magick.NET for, so this hands back raw pixels the texture upload can take directly.
export async function decodeImage(url) {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`${response.status} fetching ${url}`);

    const bitmap = await createImageBitmap(await response.blob(), { imageOrientation: "flipY" });

    // OffscreenCanvas is the only way to read pixels back out of an ImageBitmap.
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const context = canvas.getContext("2d", { willReadFrequently: true });
    context.drawImage(bitmap, 0, 0);
    const pixels = context.getImageData(0, 0, bitmap.width, bitmap.height);
    bitmap.close();

    return { width: bitmap.width, height: bitmap.height, data: pixels.data };
}

export function imageWidth(image) { return image.width; }
export function imageHeight(image) { return image.height; }

// Copies the decoded pixels into a span the caller owns. Called once per image, so the copy is not
// worth avoiding; keeping the pixels on the JS side until asked for keeps the sizes explicit.
export function copyImagePixels(image, destination) {
    destination.set(image.data.subarray(0, destination.length));
}

// -- page ---------------------------------------------------------------------

export function setStatus(text) {
    const element = document.getElementById("status");
    if (element) element.textContent = text;
}

export function reportError(message) {
    const element = document.getElementById("status");
    if (element) {
        element.textContent = "Error: " + message;
        element.classList.add("error");
    }
    console.error("[graphite] " + message);
}

export function baseUrl() {
    return document.baseURI;
}
