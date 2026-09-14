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
    // Resolved against the page rather than left relative: the browser would do this implicitly, but
    // being explicit makes the same call work anywhere fetch wants an absolute URL.
    const absolute = new URL(url, document.baseURI).href;

    const response = await fetch(absolute);
    if (!response.ok) throw new Error(`${response.status} fetching ${absolute}`);

    const bitmap = await createImageBitmap(await response.blob(), { imageOrientation: "flipY" });

    // Read the size before closing: a closed ImageBitmap reports 0 by 0, and reading it afterwards
    // produced a zero-sized texture that only failed once a real browser was involved.
    const width = bitmap.width;
    const height = bitmap.height;

    // OffscreenCanvas is the only way to read pixels back out of an ImageBitmap.
    const canvas = new OffscreenCanvas(width, height);
    const context = canvas.getContext("2d", { willReadFrequently: true });
    context.drawImage(bitmap, 0, 0);
    const pixels = context.getImageData(0, 0, width, height);
    bitmap.close();

    return { width, height, data: pixels.data };
}

export function imageWidth(image) { return image.width; }
export function imageHeight(image) { return image.height; }

// Copies the decoded pixels into a span the caller owns. Called once per image, so the copy is not
// worth avoiding; keeping the pixels on the JS side until asked for keeps the sizes explicit.
export function copyImagePixels(image, destination) {
    // getImageData hands back a Uint8ClampedArray, and the .NET memory view will only accept a plain
    // Uint8Array. This is a view over the same bytes, not another copy.
    const source = new Uint8Array(image.data.buffer, image.data.byteOffset, image.data.byteLength);
    destination.set(source.subarray(0, destination.length));
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

// Lets a sample take a setting from the URL, which is the browser's equivalent of a command line.
// Falls back to the document base so this also works where there is no location, such as a test host.
export function queryParameter(name) {
    const href = globalThis.location?.href ?? document.baseURI;
    return new URL(href).searchParams.get(name) ?? "";
}
