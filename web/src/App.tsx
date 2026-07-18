import { useEffect, useState } from "react";
import type { Stack } from "./model";
import * as api from "./api";
import { Canvas } from "./Canvas";
import { Palette } from "./Palette";
import { Inspector } from "./Inspector";

export default function App() {
  const [stack, setStack] = useState<Stack | null>(null);
  const [sel, setSel] = useState<string | null>(null);

  useEffect(() => {
    api.listStacks().then(async (list: Stack[]) => {
      setStack(list[0] ?? await api.createStack({ name: "New Stack", targetFramework: "net9.0", nodes: [], edges: [] }));
    });
  }, []);

  if (!stack) return <div>Loading…</div>;
  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh" }}>
      <div style={{ padding: 6, borderBottom: "1px solid #333" }}>
        <button onClick={() => { window.location.href = `/stacks/${stack.id}/export`; }}>Export ZIP</button>
      </div>
      <div style={{ display: "flex", flex: 1, minHeight: 0 }}>
        <Palette stack={stack} setStack={setStack} />
        <Canvas stack={stack} setStack={setStack} onSelect={setSel} />
        <Inspector stack={stack} nodeId={sel} setStack={setStack} />
      </div>
    </div>
  );
}
