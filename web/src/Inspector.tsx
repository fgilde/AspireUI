import { useState, useEffect } from "react";
import type { Stack, Node } from "./model";
import * as api from "./api";

export function Inspector({ stack, nodeId, setStack }:
  { stack: Stack; nodeId: string | null; setStack: (s: Stack) => void }) {
  const node = stack.nodes.find(n => n.id === nodeId);
  const [draft, setDraft] = useState<Node | null>(node ?? null);
  useEffect(() => setDraft(node ?? null), [nodeId]);

  if (!draft) return <div style={{ width: 300, padding: 8 }}>Select a node</div>;

  const save = () => api.patchNode(stack.id, draft).then(setStack);

  return (
    <div style={{ width: 300, borderLeft: "1px solid #333", padding: 8 }}>
      <h3>{draft.addMethod}</h3>
      <label>Name<input value={draft.resourceName}
        onChange={e => setDraft({ ...draft, resourceName: e.target.value })} /></label>
      <h4>WithCalls</h4>
      {draft.withCalls.map((w, i) => (
        <div key={i}>{w.method}({w.args.join(", ")})
          <button onClick={() => setDraft({ ...draft, withCalls: draft.withCalls.filter((_, j) => j !== i) })}>x</button>
        </div>
      ))}
      <button onClick={() => {
        const m = prompt("Method (e.g. WithDataVolume)"); if (!m) return;
        setDraft({ ...draft, withCalls: [...draft.withCalls, { method: m, args: [] }] });
      }}>+ WithCall</button>
      <hr /><button onClick={save}>Save</button>
    </div>
  );
}
