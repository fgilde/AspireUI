import { useEffect, useState } from "react";
import type { Stack } from "./model";
import * as api from "./api";

export function Palette({ stack, setStack }: { stack: Stack; setStack: (s: Stack) => void }) {
  const [cat, setCat] = useState<any[]>([]);
  useEffect(() => { api.getCatalog().then(setCat); }, []);

  const add = (rt: any) => {
    const suffix = stack.nodes.filter(x => x.addMethod === rt.addMethod).length || "";
    const varName = rt.addMethod.replace(/^Add/, "").toLowerCase() + suffix;
    const node = {
      id: "n" + crypto.randomUUID().slice(0, 8),
      varName, addMethod: rt.addMethod, resourceName: varName,
      withCalls: [], x: 40 + stack.nodes.length * 30, y: 40 + stack.nodes.length * 30,
    };
    api.saveStack({ ...stack, nodes: [...stack.nodes, node] }).then(setStack);
  };

  return (
    <div style={{ width: 200, borderRight: "1px solid #333", padding: 8 }}>
      <h3>Resources</h3>
      {cat.map(rt => (
        <button key={rt.addMethod} onClick={() => add(rt)} style={{ display: "block", width: "100%", marginBottom: 4 }}>
          {rt.label}
        </button>
      ))}
    </div>
  );
}
