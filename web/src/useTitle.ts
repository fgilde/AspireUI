import { useEffect } from "react";

// Sets the browser tab title to "AspireUI · <page>" (or just "AspireUI" when empty).
export function useTitle(page: string | null | undefined) {
  useEffect(() => {
    document.title = page ? `AspireUI · ${page}` : "AspireUI";
  }, [page]);
}
