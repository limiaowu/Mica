import type { IpcRequest, IpcResponse, IpcNotification } from './protocol';

type MessageHandler = (params: Record<string, unknown>) => void;

let nextId = 1;
const pendingRequests = new Map<number, {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}>();
const eventHandlers = new Map<string, MessageHandler[]>();

const REQUEST_TIMEOUT_MS = 5000;

// WebView2 在宿主里注入 window.chrome.webview；用 `pnpm dev` 单独跑（普通浏览器）时它不存在，
// 这里给一个降级 shim：保留监听器并暴露 window.__hostSend(msg) 供开发期手动模拟宿主下发消息
// （如 editor.load / theme.update），方便在普通浏览器里调编辑器；生产环境 WebView2 一定有真实通道。
if (!window.chrome?.webview) {
  const listeners: Array<(e: MessageEvent) => void> = [];
  (window as unknown as { chrome: { webview: unknown } }).chrome = {
    ...(window.chrome ?? {}),
    webview: {
      addEventListener: (_type: string, cb: (e: MessageEvent) => void) => { listeners.push(cb); },
      removeEventListener: (_type: string, cb: (e: MessageEvent) => void) => {
        const i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1);
      },
      postMessage: (msg: unknown) => { console.debug('[dev host←web]', msg); },
    },
  };
  // 开发期：window.__hostSend({jsonrpc:'2.0',method:'editor.load',params:{...}}) 模拟宿主通知。
  (window as unknown as { __hostSend: (m: unknown) => void }).__hostSend = (m: unknown) => {
    for (const cb of listeners) cb({ data: m } as MessageEvent);
  };
}

// Listen for messages from host
window.chrome.webview.addEventListener('message', (event: MessageEvent) => {
  const msg = event.data as IpcResponse | IpcNotification;

  if ('id' in msg && typeof msg.id === 'number') {
    // Response to a request we sent
    const pending = pendingRequests.get(msg.id);
    if (pending) {
      clearTimeout(pending.timer);
      pendingRequests.delete(msg.id);
      if ('error' in msg && msg.error) {
        pending.reject(new Error(msg.error.message));
      } else {
        pending.resolve(msg.result);
      }
    }
  } else if ('method' in msg) {
    // Notification from host
    const handlers = eventHandlers.get(msg.method);
    if (handlers) {
      for (const handler of handlers) {
        handler(msg.params ?? {});
      }
    }
  }
});

/** Send a request to host and wait for response */
export function request<T = unknown>(
  method: string,
  params?: Record<string, unknown>
): Promise<T> {
  const id = nextId++;
  const msg: IpcRequest = { jsonrpc: '2.0', id, method, params };

  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      pendingRequests.delete(id);
      reject(new Error(`IPC request '${method}' timed out after ${REQUEST_TIMEOUT_MS}ms`));
    }, REQUEST_TIMEOUT_MS);

    pendingRequests.set(id, {
      resolve: resolve as (value: unknown) => void,
      reject,
      timer,
    });

    window.chrome.webview.postMessage(msg);
  });
}

/** Send a notification to host (no response expected) */
export function notify(method: string, params?: Record<string, unknown>): void {
  const msg: IpcNotification = { jsonrpc: '2.0', method, params };
  window.chrome.webview.postMessage(msg);
}

/** Subscribe to notifications from host */
export function on(method: string, handler: MessageHandler): () => void {
  if (!eventHandlers.has(method)) {
    eventHandlers.set(method, []);
  }
  eventHandlers.get(method)!.push(handler);

  return () => {
    const handlers = eventHandlers.get(method);
    if (handlers) {
      const idx = handlers.indexOf(handler);
      if (idx >= 0) handlers.splice(idx, 1);
    }
  };
}

/** Notify host that editor content changed (debounce handled by caller) */
export function notifyContentChange(relPath: string, body: string): void {
  notify('note.save', { relPath, body });
}

/** Push word/character counts to the host status bar */
export function notifyStats(words: number, chars: number): void {
  notify('editor.stats', { words, chars });
}

/** Push the current document's heading outline to the host sidebar */
export function notifyOutline(items: { level: number; text: string }[]): void {
  notify('editor.outline', { items });
}
