// Remap monaco's deep entry points to bundled package types via side-effect imports.
declare module "monaco-editor/editor/editor.api" {
  export * from "monaco-editor";
}
declare module "monaco-editor/basic-languages/monaco.contribution";
