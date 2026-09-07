import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, CopyButton, Divider, Group, Image, PasswordInput, Stack as MStack,
  Text, TextInput,
} from "@mantine/core";
import { IconAlertCircle, IconCheck, IconCopy, IconLock, IconShieldCheck } from "@tabler/icons-react";
import QRCode from "qrcode";
import * as api from "../api";
import { useAuth } from "./AuthContext";
import { toastOk } from "../ui";

// The user's own second factor: turn it on, write down the recovery codes, turn it off again.
export function TwoFactorCard() {
  const { status, refresh } = useAuth();
  const on = !!status?.user?.twoFactor;

  const [setup, setSetup] = useState<{ secret: string; uri: string } | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [password, setPassword] = useState("");
  const [codes, setCodes] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Drawn in the browser: the secret never goes to a third party to be turned into a picture.
  useEffect(() => {
    if (!setup) { setQr(null); return; }
    QRCode.toDataURL(setup.uri, { width: 220, margin: 1 }).then(setQr).catch(() => setQr(null));
  }, [setup]);

  const message = (e: unknown, fallback: string) => {
    const m = e instanceof Error ? e.message : String(e);
    try { return (JSON.parse(m.slice(m.indexOf(": ") + 2)) as { message?: string }).message ?? fallback; }
    catch { return fallback; }
  };

  const start = async () => {
    setError(null); setBusy(true);
    try { setSetup(await api.twoFactorSetup()); }
    catch (e) { setError(message(e, "Could not start the setup")); }
    finally { setBusy(false); }
  };

  const enable = async () => {
    setError(null); setBusy(true);
    try {
      const r = await api.twoFactorEnable(code);
      setCodes(r.recoveryCodes);
      setSetup(null); setCode("");
      await refresh();
      toastOk("Two-factor authentication is on");
    } catch (e) { setError(message(e, "That code was not accepted")); }
    finally { setBusy(false); }
  };

  const disable = async () => {
    setError(null); setBusy(true);
    try {
      await api.twoFactorDisable(password);
      setPassword(""); setCodes(null);
      await refresh();
      toastOk("Two-factor authentication is off");
    } catch (e) { setError(message(e, "Could not turn it off")); }
    finally { setBusy(false); }
  };

  const newCodes = async () => {
    setError(null); setBusy(true);
    try {
      setCodes((await api.twoFactorRecoveryCodes(password)).recoveryCodes);
      setPassword("");
      toastOk("New recovery codes — the old ones no longer work");
    } catch (e) { setError(message(e, "Could not make new codes")); }
    finally { setBusy(false); }
  };

  return (
    <>
      <Divider label="Two-factor authentication" labelPosition="left" mb="md" />
      <MStack gap="md">
        <Group gap="xs">
          {on
            ? <Badge color="green" variant="light" leftSection={<IconShieldCheck size={13} />}>on</Badge>
            : <Badge color="gray" variant="light">off</Badge>}
          <Text size="sm" c="dimmed">
            {on
              ? "Signing in asks for a code from your authenticator app."
              : "A code from an authenticator app on top of your password."}
          </Text>
        </Group>

        {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={16} />}>{error}</Alert>}

        {codes && (
          <Alert color="yellow" variant="light" title="Recovery codes — write these down now">
            <Text size="xs" mb="xs">
              Each one gets you in once if your phone is gone. This is the only time they are shown.
            </Text>
            <Code block>{codes.join("\n")}</Code>
            <Group mt="xs">
              <CopyButton value={codes.join("\n")}>
                {({ copied, copy }) => (
                  <Button size="compact-xs" variant="light" leftSection={copied ? <IconCheck size={13} /> : <IconCopy size={13} />}
                    onClick={copy}>{copied ? "Copied" : "Copy"}</Button>
                )}
              </CopyButton>
              <Button size="compact-xs" variant="subtle" onClick={() => setCodes(null)}>Done</Button>
            </Group>
          </Alert>
        )}

        {!on && !setup && (
          <Group><Button leftSection={<IconLock size={16} />} loading={busy} onClick={start}>Set up</Button></Group>
        )}

        {!on && setup && (
          <MStack gap="sm">
            <Text size="sm">Scan this with your authenticator app, then type the code it shows.</Text>
            {qr
              ? <Image src={qr} w={220} h={220} alt="Two-factor QR code" />
              : <Text size="xs" c="dimmed">Could not draw the code — use the key below.</Text>}
            <Group gap="xs">
              <Text size="xs" c="dimmed">Setup key:</Text>
              <Code>{setup.secret.match(/.{1,4}/g)?.join(" ")}</Code>
              <CopyButton value={setup.secret}>
                {({ copied, copy }) => (
                  <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? "Copied" : "Copy"}</Button>
                )}
              </CopyButton>
            </Group>
            <Group align="flex-end" gap="sm">
              <TextInput label="Code" value={code} w={140} placeholder="123456" data-autofocus
                onChange={e => setCode(e.currentTarget.value)}
                onKeyDown={e => { if (e.key === "Enter") enable(); }} />
              <Button loading={busy} onClick={enable} disabled={code.replace(/\D/g, "").length !== 6}>Turn on</Button>
              <Button variant="subtle" onClick={() => { setSetup(null); setCode(""); }}>Cancel</Button>
            </Group>
          </MStack>
        )}

        {on && (
          <Group align="flex-end" gap="sm">
            <PasswordInput label="Your password" value={password} w={220}
              description="Needed to change anything here"
              onChange={e => setPassword(e.currentTarget.value)} />
            <Button variant="default" loading={busy} disabled={!password} onClick={newCodes}>New recovery codes</Button>
            <Button color="red" variant="light" loading={busy} disabled={!password} onClick={disable}>Turn off</Button>
          </Group>
        )}
      </MStack>
    </>
  );
}
