import RFB from "../lib/novnc/core/rfb.js";

// One noVNC session per console element.
const sessions = new WeakMap();

// Events can still fire while the Blazor component is being disposed (e.g. leaving the page exits
// fullscreen); by then its DotNetObjectReference may be released, which is fine to ignore.
function notify(dotNet, method, ...args) {
    dotNet.invokeMethodAsync(method, ...args).catch(() => { });
}

/**
 * Connects the element to a VNC websocket. Events go back to .NET: OnConnected, OnDisconnected(clean),
 * OnError(message) and OnDesktopName(name).
 */
export function connect(element, url, scale, dotNet) {
    disconnect(element);

    const rfb = new RFB(element, url);
    rfb.background = "#000";
    applyScale(rfb, scale);

    rfb.addEventListener("connect", () => {
        rfb.focus();
        notify(dotNet, "OnConnected");
    });
    rfb.addEventListener("disconnect", e => {
        // After disconnect() the session is already gone and .NET may have released dotNet: nothing to report.
        if (sessions.get(element) !== rfb)
            return;
        sessions.delete(element);
        notify(dotNet, "OnDisconnected", e.detail.clean);
    });
    rfb.addEventListener("securityfailure", e => notify(dotNet, "OnError", `VNC security failure: ${e.detail.reason ?? e.detail.status}`));
    rfb.addEventListener("desktopname", e => notify(dotNet, "OnDesktopName", e.detail.name));

    sessions.set(element, rfb);
}

export function disconnect(element) {
    const rfb = sessions.get(element);
    if (rfb) {
        sessions.delete(element);
        rfb.disconnect();
    }
}

/** "fit" shrinks or grows the screen to the element; "actual" shows it 1:1 and the element scrolls. */
export function setScale(element, scale) {
    const rfb = sessions.get(element);
    if (rfb)
        applyScale(rfb, scale);
}

function applyScale(rfb, scale) {
    rfb.scaleViewport = scale === "fit";
    rfb.clipViewport = false;
}

export function focus(element) {
    sessions.get(element)?.focus();
}

export function sendCtrlAltDel(element) {
    sessions.get(element)?.sendCtrlAltDel();
}

/**
 * Sends a key combination as [keysym, code] pairs: presses them in order, then releases them in reverse,
 * the way a person would. The code lets QEMU translate to scancodes independently of the keyboard layout.
 */
export function sendKeys(element, keys) {
    const rfb = sessions.get(element);
    if (!rfb)
        return;

    for (const [keysym, code] of keys)
        rfb.sendKey(keysym, code, true);
    for (const [keysym, code] of [...keys].reverse())
        rfb.sendKey(keysym, code, false);
    rfb.focus();
}

/**
 * Toggles fullscreen for the element; returns whether it is fullscreen afterwards. In the Photino
 * WebView this fills the app window. Esc goes to the VM (noVNC captures it), so leaving is done
 * with the toolbar button, which is inside the fullscreen element.
 */
export async function toggleFullscreen(element) {
    if (document.fullscreenElement) {
        await document.exitFullscreen();
        return false;
    }

    await element.requestFullscreen();
    sessions.get(element)?.focus();
    return true;
}

/**
 * Fullscreen on Windows, where requestFullscreen only fills the WebView: .NET makes the window cover the
 * monitor (Win32Fullscreen) and this makes the console cover the page, title bar and sidebar included.
 */
export function fillWindow(root, screen, fill) {
    root.classList.toggle("vm-console--fullscreen", fill);
    autoHideToolbar(root, fill);
    sessions.get(screen)?.focus();
}

// In fullscreen the toolbar floats over the screen. It slides away once the mouse has left it for
// AUTOHIDE_DELAY ms and comes back when the mouse reaches the top REVEAL_ZONE px of the screen.
const AUTOHIDE_DELAY = 2000;
const REVEAL_ZONE = 6;

function autoHideToolbar(root, enabled) {
    root._sqemuAutoHide?.();
    root._sqemuAutoHide = undefined;
    root.classList.toggle("vm-console--autohide", enabled);
    root.classList.remove("vm-console--toolbar-hidden");
    if (!enabled)
        return;

    const toolbar = root.querySelector(".vm-console__toolbar-area");
    let timer;
    let over = false;
    const show = () => {
        clearTimeout(timer);
        root.classList.remove("vm-console--toolbar-hidden");
    };
    const hideLater = () => {
        clearTimeout(timer);
        timer = setTimeout(() => root.classList.add("vm-console--toolbar-hidden"), AUTOHIDE_DELAY);
    };
    // Capture phase: noVNC stops mouse events on its canvas before they would bubble up.
    // An open menu's overlay lives inside the toolbar, so the toolbar stays while a menu is open.
    const onMove = e => {
        const nowOver = toolbar.contains(e.target) || e.clientY <= REVEAL_ZONE;
        if (nowOver === over)
            return;
        over = nowOver;
        if (over)
            show();
        else
            hideLater();
    };

    window.addEventListener("mousemove", onMove, true);
    root._sqemuAutoHide = () => {
        clearTimeout(timer);
        window.removeEventListener("mousemove", onMove, true);
    };
    // Shown on entering fullscreen so it's clear how to leave, then out of the way.
    hideLater();
}

/** Calls OnFullscreenChanged(bool) when fullscreen is entered or left, by the button or by the browser. */
export function watchFullscreen(element, dotNet) {
    const handler = () => {
        // Leaving the page removes the element, which also ends fullscreen; the component is going away then.
        const fullscreen = document.fullscreenElement === element;
        autoHideToolbar(element, fullscreen);
        if (element.isConnected)
            notify(dotNet, "OnFullscreenChanged", fullscreen);
    };
    document.addEventListener("fullscreenchange", handler);
    element._sqemuFullscreenHandler = handler;
}

export function unwatchFullscreen(element) {
    autoHideToolbar(element, false);
    if (element._sqemuFullscreenHandler)
        document.removeEventListener("fullscreenchange", element._sqemuFullscreenHandler);
}
