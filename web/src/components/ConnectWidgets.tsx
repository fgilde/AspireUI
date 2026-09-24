import { useEffect, useRef, useState } from "react";
import { Alert, Anchor, Center, Loader, Modal, Tabs } from "@mantine/core";
import { IconHeart, IconMail, IconAlertTriangle } from "@tabler/icons-react";
import { useAppTheme } from "../ThemeProvider";
import { REPO_URL } from "../GitHubLink";

const SCRIPT = "https://connect.gilde.org/widgets/v1.js";
const PROJECT = "fgilde/AspireUI";
// The widget reads this as a number and clamps it to 280-1200, so a percentage would fall back to
// its own default. The modal is sized to match.
const WIDTH = 560;

type Load = "idle" | "loading" | "ready" | "failed";

// AspireUI runs on someone else's machine, so nothing is fetched from gilde.org until a person
// actually opens the panel — and it says so plainly if that fetch does not arrive.
function useConnectScript(): Load {
  const [state, setState] = useState<Load>(() =>
    document.querySelector(`script[src="${SCRIPT}"]`) ? "ready" : "idle");

  useEffect(() => {
    if (state !== "idle") return;
    setState("loading");
    const el = document.createElement("script");
    el.type = "module";
    el.src = SCRIPT;
    el.addEventListener("load", () => setState("ready"));
    el.addEventListener("error", () => { setState("failed"); el.remove(); });
    document.head.appendChild(el);
  }, [state]);

  return state;
}

function attributes(accent: string, scheme: string, extra: Record<string, string>) {
  return {
    project: PROJECT,
    inline: "",
    theme: scheme,
    accent,
    language: "auto",
    width: String(WIDTH),
    radius: "14",
    padding: "24",
    "show-logo": "true",
    "show-homepage": "false",
    "show-preview-notice": "false",
    "show-footer": "false",
    ...extra,
  };
}

// The element is built by hand rather than in JSX: React 19 hands a custom element's props to
// matching properties, and `inline` on this one is a getter with no setter — assigning it throws
// and takes the whole tree down with it.
function Widget({ tag, extra }: { tag: string; extra: Record<string, string> }) {
  const { current } = useAppTheme();
  const state = useConnectScript();
  const host = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (state !== "ready" || !host.current) return;
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attributes(current.swatch, current.scheme, extra)))
      el.setAttribute(k, v);
    host.current.replaceChildren(el);
    // The element watches theme and accent, so a theme switch restyles it where it stands.
    return () => el.remove();
  }, [state, tag]);   // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    const el = host.current?.firstElementChild;
    if (!el) return;
    el.setAttribute("accent", current.swatch);
    el.setAttribute("theme", current.scheme);
  }, [current.swatch, current.scheme]);

  if (state === "failed") return (
    <Alert color="yellow" icon={<IconAlertTriangle size={16} />} title="The form could not be loaded">
      It comes from connect.gilde.org, which this browser could not reach. Write an issue on{" "}
      <Anchor href={`${REPO_URL}/issues`} target="_blank" rel="noreferrer">GitHub</Anchor> instead.
    </Alert>
  );

  return (
    <>
      {state !== "ready" && <Center mih={220}><Loader size="sm" /></Center>}
      <div ref={host} />
    </>
  );
}

export const ContactWidget = () =>
  <Widget tag="gilde-contact" extra={{ widget: "contact", title: "Contact AspireUI", "show-description": "false" }} />;

export const SupportWidget = () =>
  <Widget tag="gilde-support" extra={{
    widget: "support",
    title: "Support AspireUI",
    "show-description": "false",
    "support-layout": "rows",
    "show-support-icons": "true",
    "show-support-qr": "true",
    "show-support-hint": "false",
  }} />;

export function ConnectModal({ opened, tab, onClose }: {
  opened: boolean; tab: "contact" | "support"; onClose: () => void;
}) {
  const [active, setActive] = useState(tab);
  useEffect(() => { if (opened) setActive(tab); }, [opened, tab]);

  return (
    <Modal opened={opened} onClose={onClose} size={WIDTH + 64} title="Contact & support" centered>
      <Tabs value={active} onChange={v => setActive((v as "contact" | "support") ?? "contact")}>
        <Tabs.List mb="md">
          <Tabs.Tab value="contact" leftSection={<IconMail size={14} />}>Contact</Tabs.Tab>
          <Tabs.Tab value="support" leftSection={<IconHeart size={14} />}>Support</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="contact">{opened && active === "contact" && <ContactWidget />}</Tabs.Panel>
        <Tabs.Panel value="support">{opened && active === "support" && <SupportWidget />}</Tabs.Panel>
      </Tabs>
    </Modal>
  );
}
