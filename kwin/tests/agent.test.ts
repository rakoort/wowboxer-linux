import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import vm from "node:vm";

// The wowboxer-agent KWin script, run against a stand-in for KWin 6.6's scripting API (workspace, windows,
// outputs, QTimer, registerShortcut, callDBus) and a stand-in WoWBoxer app on the bus. Time is simulated: timers and
// D-Bus replies happen only when a test advances the clock, so "50 ms later" and "5 s later" are explicit steps.
const source = readFileSync(resolve(import.meta.dir, "../wowboxer-agent/contents/code/main.js"), "utf8");
const metadata = JSON.parse(readFileSync(resolve(import.meta.dir, "../wowboxer-agent/metadata.json"), "utf8"));

type Rect = { x: number; y: number; width: number; height: number };
type Handler = (...args: unknown[]) => void;

class Signal {
  private handlers: Handler[] = [];
  connect(handler: Handler) {
    this.handlers.push(handler);
  }
  emit(...args: unknown[]) {
    for (const handler of [...this.handlers]) handler(...args);
  }
}

class Clock {
  now = 1_000_000;
  timers: Timer[] = [];
}

class Timer {
  singleShot = false;
  interval = 0;
  active = false;
  due = 0;
  timeout = new Signal();
  constructor(private clock: Clock) {}
  start() {
    this.active = true;
    this.due = this.clock.now + this.interval;
  }
  stop() {
    this.active = false;
  }
}

type Output = { name: string; manufacturer: string; model: string; devicePixelRatio: number; geometry: Rect; area: Rect };

type Desktop = { name: string };

type Win = {
  internalId: string;
  pid: number;
  caption: string;
  captionNormal: string;
  resourceClass: string;
  resourceName: string;
  managed: boolean;
  deleted: boolean;
  normalWindow: boolean;
  specialWindow: boolean;
  hidden: boolean;
  minimized: boolean;
  fullScreen: boolean;
  onAllDesktops: boolean;
  desktops: Desktop[];
  move: boolean;
  resize: boolean;
  output: Output;
  frameGeometry: Rect;
  clientGeometry: Rect;
  keepAbove: boolean;
  noBorder: boolean;
  skipTaskbar: boolean;
  moves: number;
  captionNormalChanged: Signal;
  keepAboveChanged: Signal;
  interactiveMoveResizeStarted: Signal;
  interactiveMoveResizeFinished: Signal;
  frameGeometryChanged: Signal;
  clientGeometryChanged: Signal;
  decorationPolicyChanged: Signal;
  minimizedChanged: Signal;
  fullScreenChanged: Signal;
  moveResizedChanged: Signal;
  outputChanged: Signal;
  stackingOrderChanged: Signal;
};

type WindowOptions = { pid?: number; frame?: Rect; normal?: boolean; resourceClass?: string; output?: Output };

type Action = { n: number; op: string; id: string; rect?: Rect; on?: boolean };
type OwnEntry = { caption: string; rect: Rect; above: boolean; noBorder: boolean; skipTaskbar: boolean };
type Desired = {
  v: number;
  epoch: string;
  ownPid: number;
  actions: Action[];
  keepAbove: string[];
  wow: string[];
  hover: { enabled: boolean; delayMs: number; bringToFront: boolean };
  own: OwnEntry[];
};

type ReportedWindow = { id: string; caption: string; frame: Rect; keepAbove: boolean; noBorder: boolean; stack: number; output: string; moving: boolean };
type Report = {
  v: number;
  agent: string;
  epoch: string;
  done: number;
  outputs: { name: string; manufacturer: string; model: string; scale: number; geometry: Rect; workArea: Rect }[];
  active: string | null;
  windows: ReportedWindow[];
  handEdited: { id: string; frame: Rect }[];
  gaveUp: string[];
};

type Call = { service: string; method: string; args: unknown[]; reply?: Handler };

// Plasma's primary output second in KWin's list but first in screenOrder, as on a desk where the left screen is
// not the primary.
const LEFT: Output = {
  name: "HDMI-A-1",
  manufacturer: "Acme",
  model: "Side 24",
  devicePixelRatio: 1,
  geometry: { x: 0, y: 0, width: 1920, height: 1080 },
  area: { x: 0, y: 0, width: 1920, height: 1080 },
};
const MAIN: Output = {
  name: "DP-1",
  manufacturer: "Acme",
  model: "Main 27",
  devicePixelRatio: 1.5,
  geometry: { x: 1920, y: 0, width: 2560, height: 1440 },
  area: { x: 1920, y: 0, width: 2560, height: 1392 },
};

let nextId = 1;

function kwin() {
  const clock = new Clock();
  const desktop: Desktop = { name: "Desktop 1" };
  const shortcuts: Record<string, { text: string; keys: string; callback: () => void }> = {};
  const calls: Call[] = [];
  const pending: Call[] = [];
  const printed: string[] = [];
  let activations = 0;
  let raises = 0;

  // The stand-in app: owns its name while `running`; `failing` makes its methods return D-Bus errors.
  const app = { running: true, failing: false, desired: emptyDesired() };

  let active: Win | null = null;
  const workspace = {
    stackingOrder: [] as Win[],
    screens: [LEFT, MAIN],
    screenOrder: [MAIN, LEFT],
    currentDesktop: desktop,
    cursorPos: { x: 0, y: 0 },
    get activeWindow() {
      return active;
    },
    // Workspace::activateWindow: raises, then activates.
    set activeWindow(w: Win | null) {
      activations++;
      if (w) raise(w);
      active = w;
      workspace.windowActivated.emit(w);
    },
    raiseWindow: (w: Win) => {
      raises++;
      raise(w);
    },
    clientArea: (option: number, output: Output, d: Desktop) => {
      if (option !== 2 || d !== desktop) throw new Error("clientArea wants MaximizeArea, an output and the current desktop");
      return output.area;
    },
    windowAdded: new Signal(),
    windowRemoved: new Signal(),
    windowActivated: new Signal(),
    cursorPosChanged: new Signal(),
    screensChanged: new Signal(),
    screenOrderChanged: new Signal(),
    virtualScreenGeometryChanged: new Signal(),
    currentDesktopChanged: new Signal(),
  };

  function raise(w: Win) {
    const order = workspace.stackingOrder;
    order.splice(order.indexOf(w), 1);
    order.push(w);
  }

  const window = (caption: string, options: WindowOptions = {}) => {
    let frame: Rect = options.frame ?? { x: 100, y: 100, width: 800, height: 600 };
    let keepAbove = false;
    let noBorder = false;
    const w: Win = {
      internalId: `{0000000${nextId++}-0000-0000-0000-000000000000}`,
      pid: options.pid ?? 100,
      // KWin appends " <2>" to the caption of a second window with the same title; captionNormal is without.
      caption: `${caption} <2>`,
      captionNormal: caption,
      resourceClass: options.resourceClass ?? "wow.exe",
      resourceName: options.resourceClass ?? "wow.exe",
      managed: true,
      deleted: false,
      normalWindow: options.normal ?? true,
      specialWindow: false,
      hidden: false,
      minimized: false,
      fullScreen: false,
      onAllDesktops: false,
      desktops: [desktop],
      move: false,
      resize: false,
      output: options.output ?? MAIN,
      skipTaskbar: false,
      moves: 0,
      get frameGeometry() {
        return frame;
      },
      set frameGeometry(r: Rect) {
        w.moves++;
        frame = { x: r.x, y: r.y, width: r.width, height: r.height };
        w.frameGeometryChanged.emit();
      },
      get clientGeometry() {
        return { x: frame.x, y: frame.y + 30, width: frame.width, height: frame.height - 30 };
      },
      get keepAbove() {
        return keepAbove;
      },
      set keepAbove(on: boolean) {
        if (on === keepAbove) return;
        keepAbove = on;
        w.keepAboveChanged.emit(on);
      },
      get noBorder() {
        return noBorder;
      },
      set noBorder(on: boolean) {
        noBorder = on;
        w.decorationPolicyChanged.emit();
      },
      captionNormalChanged: new Signal(),
      keepAboveChanged: new Signal(),
      interactiveMoveResizeStarted: new Signal(),
      interactiveMoveResizeFinished: new Signal(),
      frameGeometryChanged: new Signal(),
      clientGeometryChanged: new Signal(),
      decorationPolicyChanged: new Signal(),
      minimizedChanged: new Signal(),
      fullScreenChanged: new Signal(),
      moveResizedChanged: new Signal(),
      outputChanged: new Signal(),
      stackingOrderChanged: new Signal(),
    };
    return w;
  };

  // KWin's callDBus: the call goes out at once; the reply (if a callback was given) comes later, and never on error.
  const callDBus = (service: string, _path: string, _iface: string, method: string, ...rest: unknown[]) => {
    const reply = typeof rest[rest.length - 1] === "function" ? (rest.pop() as Handler) : undefined;
    const call: Call = { service, method, args: rest, reply };
    calls.push(call);
    pending.push(call);
  };

  function answer(call: Call) {
    if (call.method === "NameHasOwner") return call.reply?.(app.running);
    if (!app.running || app.failing) return;
    if (call.method === "Pull") call.reply?.(JSON.stringify(app.desired));
  }

  function deliver() {
    while (pending.length > 0) answer(pending.shift() as Call);
  }

  // Lets `ms` pass: D-Bus replies arrive, then due timers fire in time order.
  function advance(ms: number) {
    const end = clock.now + ms;
    deliver();
    for (;;) {
      const due = clock.timers.filter((t) => t.active && t.due <= end).sort((a, b) => a.due - b.due)[0];
      if (!due) break;
      clock.now = due.due;
      if (due.singleShot) due.active = false;
      else due.due += due.interval;
      due.timeout.emit();
      deliver();
    }
    clock.now = end;
  }

  const context = {
    workspace,
    KWin: { MaximizeArea: 2 },
    Date: { now: () => clock.now },
    registerShortcut: (name: string, text: string, keys: string, callback: () => void) => {
      shortcuts[name] = { text, keys, callback };
      return true;
    },
    callDBus,
    print: (text: string) => printed.push(String(text)),
    QTimer: function () {
      const timer = new Timer(clock);
      clock.timers.push(timer);
      return timer;
    },
  };

  const reports = () => calls.filter((c) => c.method === "Report").map((c) => JSON.parse(String(c.args[0])) as Report);

  return {
    app,
    workspace,
    shortcuts,
    calls,
    printed,
    window,
    advance,
    reports,
    lastReport: () => reports().at(-1) as Report,
    pulls: () => calls.filter((c) => c.method === "Pull").map((c) => JSON.parse(String(c.args[0]))),
    appCalls: () => calls.filter((c) => c.service === "com.wowboxer.WoWBoxer"),
    activations: () => activations,
    raises: () => raises,
    // Windows open before the script starts, bottom of the stack first.
    open: (...windows: Win[]) => workspace.stackingOrder.push(...windows),
    start: () => {
      vm.runInNewContext(source, context);
      advance(0);
    },
    add: (w: Win) => {
      workspace.stackingOrder.push(w);
      workspace.windowAdded.emit(w);
    },
    remove: (w: Win) => {
      workspace.stackingOrder.splice(workspace.stackingOrder.indexOf(w), 1);
      w.deleted = true;
      workspace.windowRemoved.emit(w);
    },
    // Activation as a click would do it (not counted as the agent's).
    focus: (w: Win) => {
      raise(w);
      active = w;
      workspace.windowActivated.emit(w);
    },
    pointTo: (x: number, y: number) => {
      workspace.cursorPos = { x, y };
      workspace.cursorPosChanged.emit();
    },
    // The app asks the agent to pull now (kglobalaccel invokeShortcut wowboxer-agent-pull).
    invoke: (name: string) => {
      shortcuts[name].callback();
      advance(0);
    },
  };
}

function emptyDesired(): Desired {
  return { v: 1, epoch: "e1", ownPid: 0, actions: [], keepAbove: [], wow: [], hover: { enabled: false, delayMs: 0, bringToFront: true }, own: [] };
}

const at = (x: number, y: number, width = 800, height = 600): Rect => ({ x, y, width, height });

// Three clients side by side on the main output and a terminal on the left one.
function seat() {
  const k = kwin();
  const a = k.window("World of Warcraft", { pid: 11, frame: at(1920, 0) });
  const b = k.window("World of Warcraft", { pid: 12, frame: at(2720, 0) });
  const c = k.window("World of Warcraft", { pid: 13, frame: at(3520, 0) });
  const term = k.window("Terminal", { pid: 50, frame: at(0, 0), resourceClass: "konsole", output: LEFT });
  k.open(term, a, b, c);
  return { k, a, b, c, term };
}

describe("metadata", () => {
  test("is a KWin script package with the agent's id and version, off by default", () => {
    expect(metadata.KPackageStructure).toBe("KWin/Script");
    expect(metadata["X-Plasma-API"]).toBe("javascript");
    expect(metadata.KPlugin.Id).toBe("wowboxer-agent");
    expect(metadata.KPlugin.Version).toBe("1.0.0");
    expect(metadata.KPlugin.EnabledByDefault).toBe(false);
    expect(source).toContain(`var AGENT_VERSION = "${metadata.KPlugin.Version}";`);
  });
});

describe("report", () => {
  test("lists outputs in screen order with work areas, normal windows in stacking order, and the active window", () => {
    const { k, a, b, c, term } = seat();
    const popup = k.window("tooltip", { normal: false });
    k.open(popup);
    k.focus(b);
    k.start();
    const r = k.lastReport();
    expect(r.v).toBe(1);
    expect(r.agent).toBe("1.0.0");
    expect(r.epoch).toBe("e1");
    expect(r.done).toBe(0);
    expect(r.outputs).toEqual([
      { name: "DP-1", manufacturer: "Acme", model: "Main 27", scale: 1.5, geometry: MAIN.geometry, workArea: MAIN.area },
      { name: "HDMI-A-1", manufacturer: "Acme", model: "Side 24", scale: 1, geometry: LEFT.geometry, workArea: LEFT.area },
    ]);
    expect(r.active).toBe(b.internalId);
    expect(r.windows.map((w) => w.id)).toEqual([term, a, c, b].map((w) => w.internalId));
    // `stack` is the index in KWin's whole stacking order, so the popup between c and b leaves a gap.
    expect(r.windows.map((w) => w.stack)).toEqual([0, 1, 2, 4]);
    expect(r.windows[1]).toEqual({
      id: a.internalId,
      pid: 11,
      caption: "World of Warcraft",
      resourceClass: "wow.exe",
      resourceName: "wow.exe",
      frame: at(1920, 0),
      client: { x: 1920, y: 30, width: 800, height: 570 },
      keepAbove: false,
      noBorder: false,
      minimized: false,
      fullScreen: false,
      moving: false,
      output: "DP-1",
      stack: 1,
    } as ReportedWindow);
    expect(r.windows[0].output).toBe("HDMI-A-1");
    expect(r.handEdited).toEqual([]);
    expect(r.gaveUp).toEqual([]);
  });

  test("changes are reported at most every 50 ms, and with every 5 s heartbeat", () => {
    const { k, a } = seat();
    k.start();
    const before = k.reports().length;
    for (let i = 0; i < 10; i++) {
      a.frameGeometry = at(1920 + i, 0);
      k.advance(10);
    }
    // 100 ms of changes every 10 ms: two throttled reports, the last one up to date after 50 ms more.
    k.advance(50);
    expect(k.reports().length - before).toBe(2);
    expect(k.lastReport().windows.find((w) => w.id === a.internalId)?.frame).toEqual(at(1929, 0));

    const pulls = k.pulls().length;
    const reports = k.reports().length;
    k.advance(15000);
    expect(k.pulls().length - pulls).toBe(3);
    expect(k.reports().length - reports).toBe(3);
  });

  test("a window opened or closed is reported", () => {
    const { k } = seat();
    k.start();
    const late = k.window("World of Warcraft", { pid: 14 });
    k.add(late);
    k.advance(50);
    expect(k.lastReport().windows.map((w) => w.id)).toContain(late.internalId);
    k.remove(late);
    k.advance(50);
    expect(k.lastReport().windows.map((w) => w.id)).not.toContain(late.internalId);
  });
});

describe("actions", () => {
  test("Pull says which version, epoch and action it has; actions run in order, once, and done is reported", () => {
    const { k, a, b, term } = seat();
    k.app.desired.actions = [
      { n: 3, op: "border", id: a.internalId, on: false },
      { n: 1, op: "place", id: a.internalId, rect: at(1920, 0, 1688, 1392) },
      { n: 2, op: "above", id: b.internalId, on: true },
    ];
    k.start();
    expect(k.pulls()[0]).toEqual({ agent: "1.0.0", epoch: "", done: 0 });
    expect(a.frameGeometry).toEqual(at(1920, 0, 1688, 1392));
    expect(a.noBorder).toBe(true);
    expect(b.keepAbove).toBe(true);
    expect(term.moves).toBe(0);
    expect(k.lastReport().done).toBe(3);
    expect(k.activations()).toBe(0);

    // The same actions again: nothing runs twice.
    k.invoke("wowboxer-agent-pull");
    expect(k.pulls().at(-1)).toEqual({ agent: "1.0.0", epoch: "e1", done: 3 });
    expect(a.moves).toBe(1);
    k.app.desired.actions.push({ n: 4, op: "place", id: a.internalId, rect: at(1920, 0, 400, 300) });
    k.invoke("wowboxer-agent-pull");
    expect(a.moves).toBe(2);
    expect(a.frameGeometry).toEqual(at(1920, 0, 400, 300));
    expect(k.lastReport().done).toBe(4);
  });

  test("a new epoch (the app restarted) runs its actions from 1 again", () => {
    const { k, a } = seat();
    k.app.desired.actions = [{ n: 5, op: "place", id: a.internalId, rect: at(1920, 0, 500, 400) }];
    k.start();
    expect(k.lastReport().done).toBe(5);
    k.app.desired = { ...emptyDesired(), epoch: "e2", actions: [{ n: 1, op: "place", id: a.internalId, rect: at(1920, 0, 600, 450) }] };
    k.advance(5000);
    expect(a.frameGeometry).toEqual(at(1920, 0, 600, 450));
    expect(k.lastReport()).toMatchObject({ epoch: "e2", done: 1 });
  });

  test("unknown ops and unknown windows are skipped, logged and still counted as done", () => {
    const { k, a } = seat();
    k.app.desired.actions = [
      { n: 1, op: "close", id: a.internalId },
      { n: 2, op: "place", id: "{gone}", rect: at(0, 0) },
      { n: 3, op: "border", id: a.internalId, on: false },
    ];
    k.start();
    expect(k.printed.some((p) => p.includes("unknown op close"))).toBe(true);
    expect(k.printed.some((p) => p.includes("{gone}"))).toBe(true);
    expect(a.noBorder).toBe(true);
    expect(k.lastReport().done).toBe(3);
  });

  test("windows the app is not shown (panels, popups) are not acted on", () => {
    const { k } = seat();
    const panel = k.window("Panel", { normal: false });
    k.open(panel);
    k.app.desired.actions = [{ n: 1, op: "place", id: panel.internalId, rect: at(0, 0) }];
    k.start();
    expect(panel.moves).toBe(0);
  });
});

describe("keep above", () => {
  test("is set again at once when Wine clears it, at most 20 times in 10 s, then the window is reported as given up", () => {
    const { k, a, b } = seat();
    k.app.desired.keepAbove = [a.internalId];
    k.start();
    expect(a.keepAbove).toBe(true);
    expect(b.keepAbove).toBe(false);

    // Wine clears it 300–750 ms after a move: it is back before anything else happens.
    k.advance(400);
    a.keepAbove = false;
    expect(a.keepAbove).toBe(true);

    for (let i = 0; i < 18; i++) {
      k.advance(100);
      a.keepAbove = false;
    }
    expect(a.keepAbove).toBe(true);
    k.advance(100);
    a.keepAbove = false;
    expect(a.keepAbove).toBe(false);
    k.advance(50);
    expect(k.lastReport().gaveUp).toEqual([a.internalId]);

    // Given up stays given up, until the app asks again.
    k.advance(20000);
    expect(a.keepAbove).toBe(false);
    k.app.desired.actions = [{ n: 1, op: "above", id: a.internalId, on: true }];
    k.invoke("wowboxer-agent-pull");
    expect(k.lastReport().gaveUp).toEqual([]);
    a.keepAbove = false;
    expect(a.keepAbove).toBe(true);
  });

  test("restores spread over more than 10 s never give up", () => {
    const { k, a } = seat();
    k.app.desired.keepAbove = [a.internalId];
    k.start();
    for (let i = 0; i < 60; i++) {
      k.advance(600);
      a.keepAbove = false;
    }
    expect(a.keepAbove).toBe(true);
    expect(k.lastReport().gaveUp).toEqual([]);
  });

  test("a window dropped from the list is left alone", () => {
    const { k, a } = seat();
    k.app.desired.keepAbove = [a.internalId];
    k.start();
    k.app.desired.keepAbove = [];
    k.invoke("wowboxer-agent-pull");
    a.keepAbove = false;
    expect(a.keepAbove).toBe(false);
  });
});

describe("hover to activate", () => {
  function hovering(bringToFront = true) {
    const s = seat();
    s.k.app.desired.wow = [s.a.internalId, s.b.internalId];
    s.k.app.desired.hover = { enabled: true, delayMs: 30, bringToFront };
    s.k.focus(s.a);
    s.k.start();
    return s;
  }

  test("resting on another WoW window for the delay makes it active and brings it to front", () => {
    const { k, a, b } = hovering();
    // b sits under a now (a was raised by the click); point at the part of b that a does not cover.
    k.pointTo(2720 + 700, 300);
    k.advance(29);
    expect(k.workspace.activeWindow).toBe(a);
    k.advance(1);
    expect(k.workspace.activeWindow).toBe(b);
    expect(k.workspace.stackingOrder.at(-1)).toBe(b);
    expect(k.activations()).toBe(1);
  });

  test("without bring to front, the stacking stays as it was", () => {
    const { k, a, b, c, term } = hovering(false);
    b.frameGeometry = at(1920, 0, 1600, 1000);
    k.pointTo(1920 + 1000, 700);
    const before = [...k.workspace.stackingOrder];
    expect(before).toEqual([term, b, c, a]);
    k.advance(30);
    expect(k.workspace.activeWindow).toBe(b);
    expect(k.workspace.stackingOrder).toEqual(before);
  });

  test("passing over before the delay changes nothing", () => {
    const { k, a } = hovering();
    k.pointTo(2720 + 700, 300);
    k.advance(20);
    k.pointTo(1920 + 100, 300);
    k.advance(100);
    expect(k.workspace.activeWindow).toBe(a);
    expect(k.activations()).toBe(0);
  });

  test("only switches between the app's WoW windows: not to others, not from others", () => {
    const { k, a, c, term } = hovering();
    // c is not in `wow`.
    k.pointTo(3520 + 400, 300);
    k.advance(100);
    expect(k.workspace.activeWindow).toBe(a);
    // The terminal is active: resting on a WoW window does not take focus from it.
    k.focus(term);
    k.pointTo(2720 + 700, 300);
    k.advance(100);
    expect(k.workspace.activeWindow).toBe(term);
    expect(k.activations()).toBe(0);
  });

  test("never while the active window is being moved or resized, nor when hover is off", () => {
    const { k, a } = hovering();
    a.move = true;
    k.pointTo(2720 + 700, 300);
    k.advance(100);
    expect(k.workspace.activeWindow).toBe(a);
    a.move = false;

    k.app.desired.hover = { enabled: false, delayMs: 30, bringToFront: true };
    k.invoke("wowboxer-agent-pull");
    k.pointTo(2720 + 600, 300);
    k.advance(100);
    expect(k.workspace.activeWindow).toBe(a);
    expect(k.activations()).toBe(0);
  });

  test("a window covering the WoW window under the pointer blocks it", () => {
    const { k, a } = hovering();
    const browser = k.window("Browser", { pid: 60, frame: at(2720, 0, 1600, 1000), resourceClass: "firefox" });
    k.add(browser);
    k.pointTo(2720 + 700, 300);
    k.advance(100);
    expect(k.workspace.activeWindow).toBe(a);
  });
});

describe("own helper windows", () => {
  test("found by pid and caption, they get geometry, keep-above, no border and no taskbar entry, when listed and when they appear", () => {
    const { k } = seat();
    const identify = { caption: "WoWBoxer Identify 1", rect: at(10, 10, 300, 200), above: true, noBorder: true, skipTaskbar: true };
    k.app.desired.ownPid = 4242;
    k.app.desired.own = [identify];
    const mine = k.window("WoWBoxer Identify 1", { pid: 4242, normal: false });
    const sameTitleOtherApp = k.window("WoWBoxer Identify 1", { pid: 99 });
    k.open(mine, sameTitleOtherApp);
    k.start();
    expect(mine.frameGeometry).toEqual(at(10, 10, 300, 200));
    expect([mine.keepAbove, mine.noBorder, mine.skipTaskbar]).toEqual([true, true, true]);
    expect(sameTitleOtherApp.moves).toBe(0);
    expect(sameTitleOtherApp.keepAbove).toBe(false);

    // Applied once per entry: the heartbeat does not move it back.
    mine.frameGeometry = at(50, 50, 300, 200);
    k.advance(5000);
    expect(mine.frameGeometry).toEqual(at(50, 50, 300, 200));

    // A changed entry is applied again.
    k.app.desired.own = [{ ...identify, rect: at(20, 20, 300, 200) }];
    k.invoke("wowboxer-agent-pull");
    expect(mine.frameGeometry).toEqual(at(20, 20, 300, 200));

    // One that opens later is set up as it appears.
    k.app.desired.own.push({ ...identify, caption: "WoWBoxer Identify 2", rect: at(400, 10, 300, 200) });
    k.invoke("wowboxer-agent-pull");
    const second = k.window("WoWBoxer Identify 2", { pid: 4242, normal: false });
    k.add(second);
    expect(second.frameGeometry).toEqual(at(400, 10, 300, 200));
    expect(second.skipTaskbar).toBe(true);
  });
});

describe("hand edits", () => {
  test("a window moved or resized by hand is reported once, with its new frame; a cancelled move is not", () => {
    const { k, a, b } = seat();
    k.start();
    a.interactiveMoveResizeStarted.emit();
    a.move = true;
    a.frameGeometry = at(2000, 50);
    k.advance(50);
    expect(k.lastReport().windows.find((w) => w.id === a.internalId)?.moving).toBe(true);
    a.move = false;
    a.interactiveMoveResizeFinished.emit();
    k.advance(50);
    expect(k.lastReport().handEdited).toEqual([{ id: a.internalId, frame: at(2000, 50) }]);
    k.advance(5000);
    expect(k.lastReport().handEdited).toEqual([]);

    // Esc: KWin puts the old geometry back, then still says "finished".
    b.interactiveMoveResizeStarted.emit();
    b.frameGeometry = at(3000, 50);
    b.frameGeometry = at(2720, 0);
    b.interactiveMoveResizeFinished.emit();
    k.advance(50);
    expect(k.lastReport().handEdited).toEqual([]);
  });
});

describe("shortcuts", () => {
  test("are registered under kwin without keys, except show (Meta+Alt+W), and each calls Hotkey", () => {
    const k = kwin();
    k.start();
    const names = Object.keys(k.shortcuts);
    const swaps = ["next", "previous", ...Array.from({ length: 10 }, (_, i) => `client-${i + 1}`), "home"];
    expect(names.sort()).toEqual(["wowboxer-agent-pull", "wowboxer-show", ...swaps.map((s) => `wowboxer-swap-${s}`)].sort());
    for (const name of names) expect(k.shortcuts[name].keys).toBe(name === "wowboxer-show" ? "Meta+Alt+W" : "");
    expect(k.shortcuts["wowboxer-swap-client-3"].text).toBe("WoWBoxer: Client 3 to main");

    for (const s of swaps) k.invoke(`wowboxer-swap-${s}`);
    k.invoke("wowboxer-show");
    expect(k.calls.filter((c) => c.method === "Hotkey").map((c) => c.args[0])).toEqual([
      "swap:next",
      "swap:previous",
      ...Array.from({ length: 10 }, (_, i) => `swap:client:${i + 1}`),
      "swap:home",
      "show",
    ]);
  });

  test("wowboxer-agent-pull pulls at once", () => {
    const k = kwin();
    k.start();
    const pulls = k.pulls().length;
    k.invoke("wowboxer-agent-pull");
    expect(k.pulls().length).toBe(pulls + 1);
  });
});

describe("app absent", () => {
  test("not running: the agent only asks the bus whether it is there, and calls the app for nothing", () => {
    const { k, a } = seat();
    k.app.running = false;
    k.start();
    k.advance(20000);
    a.frameGeometry = at(0, 0);
    k.advance(1000);
    expect(k.appCalls()).toEqual([]);
    expect(k.calls.filter((c) => c.method === "NameHasOwner").length).toBe(5);
    expect(k.printed).toEqual([]);

    // It starts: the next heartbeat finds it.
    k.app.running = true;
    k.advance(5000);
    expect(k.pulls().length).toBe(1);
    expect(k.reports().length).toBe(1);
  });

  test("gone after it set things up: the agent keeps the last desired state and stays quiet", () => {
    const { k, a, b } = seat();
    k.app.desired.keepAbove = [a.internalId];
    k.app.desired.wow = [a.internalId, b.internalId];
    k.app.desired.hover = { enabled: true, delayMs: 30, bringToFront: true };
    k.focus(a);
    k.start();
    k.app.failing = true;
    k.advance(10000);
    a.keepAbove = false;
    expect(a.keepAbove).toBe(true);
    k.pointTo(2720 + 700, 300);
    k.advance(30);
    expect(k.workspace.activeWindow).toBe(b);
    expect(k.printed).toEqual([]);

    // Once the next heartbeat finds its name gone, nothing is sent to it any more.
    k.app.running = false;
    k.advance(5000);
    const calls = k.appCalls().length;
    k.advance(10000);
    b.frameGeometry = at(0, 0);
    k.advance(1000);
    expect(k.appCalls().length).toBe(calls);
  });

  test("an empty desired state does nothing", () => {
    const { k, a, b, c } = seat();
    k.focus(a);
    k.start();
    k.pointTo(2720 + 700, 300);
    k.advance(10000);
    for (const w of [a, b, c]) {
      expect(w.moves).toBe(0);
      expect(w.keepAbove).toBe(false);
      expect(w.noBorder).toBe(false);
    }
    expect(k.activations()).toBe(0);
  });
});
