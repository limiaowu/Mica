interface ChromeWebView extends EventTarget {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

interface Chrome {
  webview: ChromeWebView;
}

interface Window {
  chrome: Chrome;
}

declare module '*.css' {
  const content: string;
  export default content;
}
