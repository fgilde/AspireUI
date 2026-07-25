import { useEffect, useReducer, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Container, Group, Title, Anchor, Text, Tooltip, ActionIcon, Divider, SegmentedControl } from "@mantine/core";
import { IconBrandGithub, IconHelp, IconArrowLeft, IconLayoutGrid, IconLayoutDashboard } from "@tabler/icons-react";
import { APP_VERSION, BUILD_INFO } from "../model";
import { UserMenu } from "../auth/UserMenu";
import { useViewMode, type ViewMode } from "../viewMode";
import logo from "../assets/logo.svg";
import wordmark from "../assets/wordmark.svg";

// Page shell providing consistent header/footer chrome with optional back button and title.
export function PageShell({ title, back = true, actions, children, container = "xl" }: {
  title?: ReactNode; back?: boolean; actions?: ReactNode; children: ReactNode;
  container?: "xl" | "lg" | "md" | "sm" | false;
}) {
  const nav = useNavigate();
  const { mode, canToggle, setMode } = useViewMode();
  const [, bump] = useReducer(x => x + 1, 0);
  useEffect(() => { const h = () => bump(); window.addEventListener("aspireui:mode-changed", h); return () => window.removeEventListener("aspireui:mode-changed", h); }, []);
  const switchMode = (m: ViewMode) => { setMode(m); nav("/"); };
  return (
    <AppShell header={{ height: 86 }} footer={{ height: 36 }} padding="lg">
      <AppShell.Header withBorder>
        <Container size="xl" h="100%">
          <Group h="100%" justify="space-between" wrap="nowrap">
            <Group gap="sm" wrap="nowrap" style={{ flex: 1, minWidth: 0 }}>
              <Anchor onClick={() => nav("/")} style={{ display: "flex", cursor: "pointer" }}>
                <img src={wordmark} alt="AspireUI" height={80} style={{ display: "block" }} />
              </Anchor>
              {title && <>
                {back && <Tooltip label="Back to apps" withArrow>
                  <ActionIcon variant="subtle" color="gray" onClick={() => nav("/")} aria-label="Back"><IconArrowLeft size={18} /></ActionIcon>
                </Tooltip>}
                <Divider orientation="vertical" my={20} />
                <Title order={4} fw={600}>{title}</Title>
              </>}
            </Group>
            {canToggle && (
              <SegmentedControl size="sm" value={mode} onChange={v => switchMode(v as ViewMode)}
                data={[
                  { value: "simple", label: <Group gap={6} wrap="nowrap"><IconLayoutGrid size={15} /> My apps</Group> },
                  { value: "full", label: <Group gap={6} wrap="nowrap"><IconLayoutDashboard size={15} /> Builder</Group> },
                ]} />
            )}
            <Group gap="sm" wrap="nowrap" style={{ flex: 1, minWidth: 0, justifyContent: "flex-end" }}>
              {actions}
              <UserMenu />
            </Group>
          </Group>
        </Container>
      </AppShell.Header>

      <AppShell.Main>
        {container ? <Container size={container}>{children}</Container> : children}
      </AppShell.Main>

      <AppShell.Footer>
        <Container size="xl" h="100%">
          <Group h="100%" justify="center" gap={8}>
            <img src={logo} alt="" height={18} style={{ display: "block" }} />
            <Tooltip label={`build ${BUILD_INFO}`} withArrow><Text size="xs" c="dimmed">AspireUI v{APP_VERSION}</Text></Tooltip>
            <Text size="xs" c="dimmed">·</Text>
            <Tooltip label="GitHub" withArrow>
              <ActionIcon component="a" href="https://github.com/fgilde/AspireUI" target="_blank" rel="noreferrer" variant="subtle" color="gray" size="sm" aria-label="GitHub">
                <IconBrandGithub size={15} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Documentation" withArrow>
              <ActionIcon component="a" href="https://github.com/fgilde/AspireUI/tree/master/docs" target="_blank" rel="noreferrer" variant="subtle" color="gray" size="sm" aria-label="Documentation">
                <IconHelp size={15} />
              </ActionIcon>
            </Tooltip>
            <Text size="xs" c="dimmed">·</Text>
            <Anchor size="xs" c="dimmed" href="https://www.gilde.org" target="_blank" rel="noreferrer">by gilde.org</Anchor>
          </Group>
        </Container>
      </AppShell.Footer>
    </AppShell>
  );
}
