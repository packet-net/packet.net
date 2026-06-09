/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_MODE?: "mock" | "live";
  readonly VITE_API_PROXY?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
