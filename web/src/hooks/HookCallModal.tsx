import { useState } from "react";
import type { ChangeEvent } from "react";
import { Modal, Stack, Group, Button, TextInput, PasswordInput, Text, Alert, Anchor, Code, Title } from "@mantine/core";
import { IconPlayerPlay, IconAlertTriangle, IconCheck } from "@tabler/icons-react";
import { useNavigate } from "react-router-dom";
import * as api from "../api";
import { callParams } from "./curl";

export function HookCallModal({ row, targetName, onClose, onDone }: {
  row: api.HookRow; targetName: string; onClose: () => void; onDone: () => void;
}) {
  const nav = useNavigate();
  const params = callParams(row.hook.params);
  const [vals, setVals] = useState<Record<string, string>>({});
  const [step, setStep] = useState<"input" | "confirm" | "result">(params.length ? "input" : "confirm");
  const [busy, setBusy] = useState(false);
  const [res, setRes] = useState<api.HookResult | null>(null);
  const missing = params.some(p => p.mode === "required" && !(vals[p.key] ?? "").trim());

  const call = async () => {
    setBusy(true);
    try {
      const args = Object.fromEntries(Object.entries(vals).filter(([, v]) => v.trim() !== ""));
      setRes(await api.callHook(row.webhookPath, args));
      setStep("result");
      onDone();
    } finally { setBusy(false); }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={<Title order={5}>Call {row.hook.name}</Title>}>
      {step === "input" && (
        <Stack gap="sm">
          {params.map(p => {
            const props = {
              label: p.key, withAsterisk: p.mode === "required", value: vals[p.key] ?? "",
              placeholder: p.mode === "optional" ? (p.secret && p.value ? "stored default" : p.value ?? "") : "",
              onChange: (e: ChangeEvent<HTMLInputElement>) => { const v = e.currentTarget.value; setVals(s => ({ ...s, [p.key]: v })); },
            };
            return p.secret ? <PasswordInput key={p.key} {...props} /> : <TextInput key={p.key} {...props} />;
          })}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>Cancel</Button>
            <Button disabled={missing} onClick={() => setStep("confirm")}>Continue</Button>
          </Group>
        </Stack>
      )}
      {step === "confirm" && (
        <Stack gap="sm">
          <Alert color="orange" icon={<IconAlertTriangle size={16} />}>
            This creates a new instance of <b>{row.source}</b> on <b>{targetName}</b>
            {row.hook.expireDays >= 0 ? `, deleted automatically after ${row.hook.expireDays} days.` : ", kept until you delete it."}
          </Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => (params.length ? setStep("input") : onClose())}>Back</Button>
            <Button color="orange" loading={busy} leftSection={<IconPlayerPlay size={16} />} onClick={call}>Call hook now</Button>
          </Group>
        </Stack>
      )}
      {step === "result" && res && (
        <Stack gap="sm">
          <Alert color={res.success ? (res.error ? "yellow" : "teal") : "red"} icon={res.success ? <IconCheck size={16} /> : <IconAlertTriangle size={16} />}
            title={res.success ? "Instance created" : `Failed (HTTP ${res.status})`}>
            {res.error && <Code block mah={240} style={{ overflow: "auto" }}>{res.error}</Code>}
            {res.url && <Text size="sm" mt={4}>URL: <Anchor href={res.url} target="_blank" rel="noreferrer">{res.url}</Anchor></Text>}
          </Alert>
          <Group justify="flex-end">
            {res.stackId && <Button variant="light" onClick={() => nav(`/app/${res.stackId}`)}>Open instance</Button>}
            <Button onClick={onClose}>Close</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
