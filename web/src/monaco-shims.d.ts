// monaco's package "exports" remap ./* -> ./esm/vs/*.js. These deep entry points ship no bundled
// .d.ts, so alias the API one to the full package types; the contribution is a side-effect import.
declare module "monaco-editor/editor/editor.api" {
  export * from "monaco-editor";
}
declare module "monaco-editor/basic-languages/monaco.contribution";
