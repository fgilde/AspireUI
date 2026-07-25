// Inline sparkline for editor dashboard, hosting page, and overview displays.
export function Spark({ values, color, w = 46, h = 14 }: { values: number[]; color: string; w?: number; h?: number }) {
  if (values.length < 2) return null;
  const max = Math.max(1, ...values);
  const pts = values.map((v, i) => `${(i / (values.length - 1)) * w},${h - (v / max) * h}`).join(" ");
  return <svg width={w} height={h} style={{ display: "block" }}><polyline points={pts} fill="none" stroke={color} strokeWidth={1.5} /></svg>;
}
