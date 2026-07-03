// JSON-RPC 2.0 message types for Mica IPC

export interface IpcRequest {
  jsonrpc: '2.0';
  id?: number;
  method: string;
  params?: Record<string, unknown>;
}

export interface IpcResponse {
  jsonrpc: '2.0';
  id: number;
  result?: unknown;
  error?: { code: number; message: string };
}

export interface IpcNotification {
  jsonrpc: '2.0';
  method: string;
  params?: Record<string, unknown>;
}

export type IpcMessage = IpcRequest | IpcResponse | IpcNotification;

// Host → Web
export interface EditorLoadParams {
  relPath: string;
  body: string;
}

export interface ThemeUpdateParams {
  vars: Record<string, string>;
  mode: 'light' | 'dark';
}

// Web → Host
export interface NoteSaveParams {
  relPath: string;
  body: string;
}

export interface NoteSaveResult {
  savedAt: number;
}

// 粘贴/拖入图片 → 宿主落盘（image.save 请求）
export interface ImageSaveParams {
  noteAbsPath: string;  // 当前笔记绝对路径（= editor.load 的 relPath）
  dataBase64: string;   // 图片字节的 base64（无 data URI 前缀）
  ext: string;          // 扩展名（png/jpg/…）
  sourceName?: string;  // 原文件名（拖入文件时有；剪贴板位图通常为空）
}

export interface ImageSaveResult {
  src: string;          // 要写进 .md 的 src：相对笔记目录的路径，或 data URI（内嵌模式）
}
