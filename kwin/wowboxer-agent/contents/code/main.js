// wowboxer-agent: WoWBoxer's hands inside KWin. The WoWBoxer app decides where the World of Warcraft windows go;
// this script reports the screens and windows to it and carries out what it asks. Protocol v1:
// docs/linux/port-plan.md, "The agent protocol".
//
// It only moves, resizes and restyles windows (frameGeometry, keepAbove, noBorder, skipTaskbar). It never sends a
// key, click or pointer event, and never activates a window except for hover to activate, between the app's WoW
// windows only, while one of them is already active.

var AGENT_VERSION = "1.0.0";
var APP_SERVICE = "com.wowboxer.WoWBoxer";
var APP_PATH = "/Agent";
var APP_INTERFACE = "com.wowboxer.Agent1";
var BUS = "org.freedesktop.DBus";

var HEARTBEAT_MS = 5000;
var REPORT_MS = 50;
// A window whose keep-above is cleared again and again is left alone after this many restores in the window below,
// so the agent never fights a client forever.
var MAX_CORRECTIONS = 20;
var CORRECTION_WINDOW_MS = 10000;

// The last desired state from the app; empty = do nothing. Kept when the app goes away.
var desired = { ownPid: 0, keepAbove: {}, wow: {}, hover: { enabled: false, delayMs: 0, bringToFront: true }, own: [] };
var epoch = "";
var done = 0;
var appPresent = false;

var watched = {};       // id -> true once its signals are connected
var corrections = {};   // id -> timestamps of recent keep-above restores
var gaveUp = {};        // id -> true once its keep-above is no longer restored
var moveStart = {};     // id -> frame when an interactive move/resize started
var handEdited = {};    // id -> frame after a move/resize by hand, since the last report
var ownApplied = {};    // id -> the own entry (as JSON) last applied to that helper window
var hoverTarget = null;

var reportTimer = new QTimer();
reportTimer.singleShot = true;
reportTimer.interval = REPORT_MS;
reportTimer.timeout.connect(sendReport);

var heartbeat = new QTimer();
heartbeat.singleShot = false;
heartbeat.interval = HEARTBEAT_MS;
heartbeat.timeout.connect(pull);

var hoverTimer = new QTimer();
hoverTimer.singleShot = true;
hoverTimer.timeout.connect(onHoverTimer);

// QUuid reaches scripts as a value whose string form is "{…}", the id the app uses.
function idOf(w) {
    return String(w.internalId);
}

// captionNormal is the title without the " <2>" KWin appends to windows that share one.
function captionOf(w) {
    return String(w.captionNormal !== undefined ? w.captionNormal : w.caption);
}

function rectOf(r) {
    return { x: r.x, y: r.y, width: r.width, height: r.height };
}

function sameRect(a, b) {
    return a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;
}

// The windows the app sees and may act on: managed normal windows (no panels, docks or popups).
function isReported(w) {
    return !!w && w.managed && !w.deleted && w.normalWindow && !w.specialWindow;
}

function reportedWindows() {
    var order = workspace.stackingOrder;
    var list = [];
    for (var i = 0; i < order.length; i++) {
        if (isReported(order[i])) list.push(order[i]);
    }
    return list;
}

function windowById(id) {
    var list = reportedWindows();
    for (var i = 0; i < list.length; i++) {
        if (idOf(list[i]) === id) return list[i];
    }
    return null;
}

// ---- D-Bus ---------------------------------------------------------------------------------------------------

// KWin's callDBus never calls the callback when the call fails, but logs a warning for every failed call that has
// one (scripting.cpp, Script::callDBus). Asking the bus whether the app owns its name first keeps the journal
// quiet while the app is not running; calls without a callback (Report, Hotkey) log nothing either way.
function pull() {
    callDBus(BUS, "/org/freedesktop/DBus", BUS, "NameHasOwner", APP_SERVICE, function (owned) {
        appPresent = !!owned;
        if (!appPresent) return;
        var hello = JSON.stringify({ agent: AGENT_VERSION, epoch: epoch, done: done });
        callDBus(APP_SERVICE, APP_PATH, APP_INTERFACE, "Pull", hello, onDesired);
    });
}

function sendReport() {
    reportTimer.stop();
    if (!appPresent) return;
    callDBus(APP_SERVICE, APP_PATH, APP_INTERFACE, "Report", JSON.stringify(state()));
    handEdited = {};
}

// At most one report per REPORT_MS while things change.
function scheduleReport() {
    if (appPresent && !reportTimer.active) reportTimer.start();
}

function hotkey(action) {
    callDBus(APP_SERVICE, APP_PATH, APP_INTERFACE, "Hotkey", action);
}

// ---- report --------------------------------------------------------------------------------------------------

function outputs() {
    // screenOrder is Plasma's order (the primary first); clientArea needs an output and a desktop together.
    var order = workspace.screenOrder;
    var list = [];
    for (var i = 0; i < order.length; i++) {
        var o = order[i];
        list.push({
            name: String(o.name),
            manufacturer: String(o.manufacturer),
            model: String(o.model),
            scale: o.devicePixelRatio,
            geometry: rectOf(o.geometry),
            workArea: rectOf(workspace.clientArea(KWin.MaximizeArea, o, workspace.currentDesktop)),
        });
    }
    return list;
}

function state() {
    var all = workspace.stackingOrder;
    var windows = [];
    for (var i = 0; i < all.length; i++) {
        var w = all[i];
        if (!isReported(w)) continue;
        windows.push({
            id: idOf(w),
            pid: w.pid,
            caption: captionOf(w),
            resourceClass: String(w.resourceClass),
            resourceName: String(w.resourceName),
            frame: rectOf(w.frameGeometry),
            client: rectOf(w.clientGeometry),
            keepAbove: w.keepAbove,
            noBorder: w.noBorder,
            minimized: w.minimized,
            fullScreen: w.fullScreen,
            moving: w.move || w.resize,
            output: w.output ? String(w.output.name) : "",
            stack: i,
        });
    }
    var edited = [];
    for (var id in handEdited) edited.push({ id: id, frame: handEdited[id] });
    var stopped = [];
    for (var g in gaveUp) stopped.push(g);
    var active = workspace.activeWindow;
    return {
        v: 1,
        agent: AGENT_VERSION,
        epoch: epoch,
        done: done,
        outputs: outputs(),
        active: active ? idOf(active) : null,
        windows: windows,
        handEdited: edited,
        gaveUp: stopped,
    };
}

// ---- desired state -------------------------------------------------------------------------------------------

function setOf(ids) {
    var set = {};
    for (var i = 0; i < (ids || []).length; i++) set[String(ids[i])] = true;
    return set;
}

function onDesired(text) {
    var d;
    try {
        d = JSON.parse(String(text));
    } catch (e) {
        print("wowboxer-agent: Pull reply is not JSON: " + e);
        return;
    }
    if (!d || d.v !== 1) {
        print("wowboxer-agent: Pull reply is not protocol v1");
        return;
    }
    if (d.epoch !== epoch) {
        // A new app run: its action numbers start again, and old give-ups are its to retry.
        epoch = String(d.epoch);
        done = 0;
        corrections = {};
        gaveUp = {};
        ownApplied = {};
    }
    var hover = d.hover || {};
    desired = {
        ownPid: d.ownPid || 0,
        keepAbove: setOf(d.keepAbove),
        wow: setOf(d.wow),
        hover: { enabled: !!hover.enabled, delayMs: Math.max(0, hover.delayMs || 0), bringToFront: hover.bringToFront !== false },
        own: d.own || [],
    };
    // Windows no longer listed are not restored any more, so they are not "given up" either.
    for (var id in gaveUp) {
        if (!desired.keepAbove[id]) delete gaveUp[id];
    }
    for (var c in corrections) {
        if (!desired.keepAbove[c]) delete corrections[c];
    }
    if (!desired.hover.enabled) cancelHover();

    var actions = (d.actions || []).slice().sort(function (a, b) { return a.n - b.n; });
    for (var i = 0; i < actions.length; i++) {
        if (!(actions[i].n > done)) continue;
        run(actions[i]);
        done = actions[i].n;
    }

    var list = reportedWindows();
    for (var j = 0; j < list.length; j++) holdAbove(list[j]);
    applyOwnAll();
    sendReport();
}

function run(a) {
    var w = windowById(String(a.id));
    if (!w) {
        print("wowboxer-agent: action " + a.n + " (" + a.op + "): no window " + a.id);
        return;
    }
    switch (a.op) {
    case "place":
        // Setting frameGeometry never activates or raises the window.
        if (a.rect) w.frameGeometry = { x: a.rect.x, y: a.rect.y, width: a.rect.width, height: a.rect.height };
        break;
    case "above":
        if (a.on) {
            // The app asks again: restoring starts afresh.
            delete corrections[idOf(w)];
            delete gaveUp[idOf(w)];
        }
        w.keepAbove = !!a.on;
        break;
    case "border":
        w.noBorder = !a.on;
        break;
    default:
        print("wowboxer-agent: action " + a.n + ": unknown op " + a.op);
    }
}

// ---- keep above ----------------------------------------------------------------------------------------------

// True if the window's keep-above may be restored again; false once it was restored too often.
function allowCorrection(id) {
    var now = Date.now();
    var recent = (corrections[id] || []).filter(function (t) { return now - t < CORRECTION_WINDOW_MS; });
    if (recent.length >= MAX_CORRECTIONS) {
        corrections[id] = recent;
        return false;
    }
    recent.push(now);
    corrections[id] = recent;
    return true;
}

// Wine clears "keep above" 300–750 ms after it moves or resizes a window it does not think is topmost (seen on a
// Proton client under XWayland), so keepAboveChanged brings the agent back here to set it again at once.
function holdAbove(w) {
    var id = idOf(w);
    if (!desired.keepAbove[id] || gaveUp[id] || w.keepAbove) return;
    if (allowCorrection(id)) {
        w.keepAbove = true;
        return;
    }
    gaveUp[id] = true;
    print("wowboxer-agent: " + id + " keeps clearing keep-above; left as it is");
    scheduleReport();
}

// ---- the app's own helper windows ----------------------------------------------------------------------------

function ownEntryFor(w) {
    if (!desired.ownPid || !w.managed || w.deleted || w.pid !== desired.ownPid) return null;
    var caption = captionOf(w);
    for (var i = 0; i < desired.own.length; i++) {
        if (desired.own[i].caption === caption) return desired.own[i];
    }
    return null;
}

function applyOwn(w) {
    var entry = ownEntryFor(w);
    var id = idOf(w);
    if (!entry) {
        delete ownApplied[id];
        return;
    }
    var key = JSON.stringify(entry);
    if (ownApplied[id] === key) return;
    ownApplied[id] = key;
    if (entry.rect) w.frameGeometry = { x: entry.rect.x, y: entry.rect.y, width: entry.rect.width, height: entry.rect.height };
    w.keepAbove = !!entry.above;
    w.noBorder = !!entry.noBorder;
    w.skipTaskbar = !!entry.skipTaskbar;
}

function applyOwnAll() {
    var all = workspace.stackingOrder;
    for (var i = 0; i < all.length; i++) applyOwn(all[i]);
}

// ---- windows -------------------------------------------------------------------------------------------------

function watch(w) {
    var id = idOf(w);
    if (watched[id]) return;
    watched[id] = true;
    w.captionNormalChanged.connect(function () {
        applyOwn(w);
        scheduleReport();
    });
    w.keepAboveChanged.connect(function () {
        holdAbove(w);
        scheduleReport();
    });
    w.interactiveMoveResizeStarted.connect(function () {
        moveStart[id] = rectOf(w.frameGeometry);
    });
    // Also emitted when the user cancels with Esc (the old geometry is back by then), so only a real change counts.
    w.interactiveMoveResizeFinished.connect(function () {
        var start = moveStart[id];
        delete moveStart[id];
        var frame = rectOf(w.frameGeometry);
        if (isReported(w) && (!start || !sameRect(start, frame))) handEdited[id] = frame;
        scheduleReport();
    });
    var changes = [w.frameGeometryChanged, w.clientGeometryChanged, w.decorationPolicyChanged, w.minimizedChanged,
        w.fullScreenChanged, w.moveResizedChanged, w.outputChanged, w.stackingOrderChanged];
    for (var i = 0; i < changes.length; i++) changes[i].connect(scheduleReport);
}

function onWindowAdded(w) {
    watch(w);
    applyOwn(w);
    if (isReported(w)) holdAbove(w);
    scheduleReport();
}

function onWindowRemoved(w) {
    var id = idOf(w);
    delete watched[id];
    delete corrections[id];
    delete gaveUp[id];
    delete moveStart[id];
    delete handEdited[id];
    delete ownApplied[id];
    if (hoverTarget === w) cancelHover();
    scheduleReport();
}

// ---- hover to activate ---------------------------------------------------------------------------------------

function isWow(w) {
    return isReported(w) && !!desired.wow[idOf(w)];
}

function contains(r, p) {
    return p.x >= r.x && p.x < r.x + r.width && p.y >= r.y && p.y < r.y + r.height;
}

function shownHere(w) {
    if (w.deleted || w.minimized || w.hidden) return false;
    if (w.onAllDesktops || w.desktops.length === 0) return true;
    return w.desktops.indexOf(workspace.currentDesktop) >= 0;
}

// The WoW window that is topmost under the point, or null when anything else (another app, a panel, the
// desktop) is what the pointer is over.
function wowAt(p) {
    var order = workspace.stackingOrder;
    for (var i = order.length - 1; i >= 0; i--) {
        var w = order[i];
        if (!shownHere(w) || !contains(w.frameGeometry, p)) continue;
        return isWow(w) ? w : null;
    }
    return null;
}

function cancelHover() {
    hoverTarget = null;
    hoverTimer.stop();
}

// Focus only moves from one WoW window to another: while any other window is active, or one is being moved or
// resized, the pointer passing over a game changes nothing.
function onCursor() {
    var active = workspace.activeWindow;
    if (!desired.hover.enabled || !isWow(active) || active.move || active.resize) {
        cancelHover();
        return;
    }
    var target = wowAt(workspace.cursorPos);
    if (!target || target === active) {
        cancelHover();
        return;
    }
    if (target === hoverTarget && hoverTimer.active) return;
    hoverTarget = target;
    hoverTimer.interval = desired.hover.delayMs;
    hoverTimer.start();
}

// Setting workspace.activeWindow is Workspace::activateWindow, which always raises the window
// (activation.cpp). Without "bring to front" the windows that were above it are raised again in their old order,
// which puts the stack back as it was; raising changes the stacking only and activates nothing.
function onHoverTimer() {
    var target = hoverTarget;
    hoverTarget = null;
    if (!desired.hover.enabled || !isWow(target)) return;
    var active = workspace.activeWindow;
    if (!isWow(active) || target === active || active.move || active.resize) return;
    if (wowAt(workspace.cursorPos) !== target) return;
    var above = [];
    if (!desired.hover.bringToFront) {
        var order = workspace.stackingOrder;
        for (var i = order.indexOf(target) + 1; i < order.length; i++) {
            if (!order[i].deleted) above.push(order[i]);
        }
    }
    workspace.activeWindow = target;
    for (var j = 0; j < above.length; j++) workspace.raiseWindow(above[j]);
}

// ---- shortcuts -----------------------------------------------------------------------------------------------

// registerShortcut hands the key to KGlobalAccel::setShortcut with autoloading, so a key the app stored with
// kglobalaccel setForeignShortcutKeys wins over the default here; "" registers the action without a key, and the
// app can still trigger it with invokeShortcut.
registerShortcut("wowboxer-agent-pull", "WoWBoxer: fetch the app's window state (used by the app)", "", pull);

function swapShortcut(name, text, action) {
    registerShortcut(name, "WoWBoxer: " + text, "", function () { hotkey(action); });
}
swapShortcut("wowboxer-swap-next", "Next client to main", "swap:next");
swapShortcut("wowboxer-swap-previous", "Previous client to main", "swap:previous");
for (var n = 1; n <= 10; n++) swapShortcut("wowboxer-swap-client-" + n, "Client " + n + " to main", "swap:client:" + n);
swapShortcut("wowboxer-swap-home", "Back to home layout", "swap:home");
registerShortcut("wowboxer-show", "WoWBoxer: show the app", "Meta+Alt+W", function () { hotkey("show"); });

// ---- start ---------------------------------------------------------------------------------------------------

workspace.windowAdded.connect(onWindowAdded);
workspace.windowRemoved.connect(onWindowRemoved);
workspace.windowActivated.connect(function () {
    cancelHover();
    scheduleReport();
});
workspace.cursorPosChanged.connect(onCursor);
workspace.screensChanged.connect(scheduleReport);
workspace.screenOrderChanged.connect(scheduleReport);
workspace.virtualScreenGeometryChanged.connect(scheduleReport);
workspace.currentDesktopChanged.connect(scheduleReport);

(function adoptOpenWindows() {
    var open = workspace.stackingOrder;
    for (var i = 0; i < open.length; i++) watch(open[i]);
})();

heartbeat.start();
pull();
