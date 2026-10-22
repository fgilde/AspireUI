import { useState } from "react";
import { Autocomplete, Button, Checkbox, Group, PasswordInput, Text } from "@mantine/core";
import { modals } from "@mantine/modals";
import * as api from "../api";
import type { Stack } from "../model";
import { toastErr, toastOk } from "../ui";

function ask<T>(title: string, body: (done: (v: T | null) => void) => React.ReactNode): Promise<T | null> {
  return new Promise(resolve => {
    let settled = false;
    const done = (v: T | null) => { if (settled) return; settled = true; modals.closeAll(); resolve(v); };
    modals.open({ title, children: body(done), onClose: () => done(null) });
  });
}

function CategoryBody({ initial, categories, done }: { initial: string; categories: string[]; done: (v: string | null) => void }) {
  const [val, setVal] = useState(initial);
  return (
    <>
      <Autocomplete label="Category" description="Pick one or type a new name to create it" data={categories}
        value={val} onChange={setVal} data-autofocus
        onKeyDown={e => { if (e.key === "Enter") done(val.trim()); }} />
      <Group justify="space-between" mt="md">
        <Button variant="subtle" color="gray" disabled={!initial} onClick={() => done("")}>Remove from category</Button>
        <Group gap="xs">
          <Button variant="default" onClick={() => done(null)}>Cancel</Button>
          <Button disabled={!val.trim()} onClick={() => done(val.trim())}>Move</Button>
        </Group>
      </Group>
    </>
  );
}

export async function moveToCategory(s: Stack, categories: string[]) {
  const c = await ask<string>(`Move "${s.name}" to a category`, done =>
    <CategoryBody initial={s.category ?? ""} categories={categories} done={done} />);
  if (c === null) return false;
  try { await api.setStackCategory(s.id, c || null); toastOk(c ? `Moved to "${c}"` : "Removed from its category"); return true; }
  catch (e) { toastErr(e); return false; }
}

function LockBody({ done }: { done: (v: boolean | null) => void }) {
  const [needsPassword, setNeedsPassword] = useState(false);
  return (
    <>
      <Text size="sm">While locked, nothing about this app can be changed: no start, stop, redeploy, edit, settings or delete.</Text>
      <Checkbox mt="md" label="Require my password to unlock" checked={needsPassword}
        onChange={e => setNeedsPassword(e.currentTarget.checked)} />
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={() => done(null)}>Cancel</Button>
        <Button onClick={() => done(needsPassword)}>Lock</Button>
      </Group>
    </>
  );
}

function UnlockBody({ done }: { done: (v: string | null) => void }) {
  const [pw, setPw] = useState("");
  return (
    <>
      <PasswordInput label="Your password" value={pw} data-autofocus onChange={e => setPw(e.currentTarget.value)}
        onKeyDown={e => { if (e.key === "Enter" && pw) done(pw); }} />
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={() => done(null)}>Cancel</Button>
        <Button disabled={!pw} onClick={() => done(pw)}>Unlock</Button>
      </Group>
    </>
  );
}

export async function lockApp(s: Stack) {
  const needsPassword = await ask<boolean>(`Lock "${s.name}"`, done => <LockBody done={done} />);
  if (needsPassword === null) return false;
  try { await api.lockStack(s.id, needsPassword); toastOk(`"${s.name}" is locked`); return true; }
  catch (e) { toastErr(e); return false; }
}

export async function unlockApp(s: Stack) {
  let password: string | undefined;
  if (s.lockNeedsPassword) {
    const pw = await ask<string>(`Unlock "${s.name}"`, done => <UnlockBody done={done} />);
    if (pw === null) return false;
    password = pw;
  }
  try { await api.unlockStack(s.id, password); toastOk(`"${s.name}" is unlocked`); return true; }
  catch (e) { toastErr(e, "Could not unlock"); return false; }
}
