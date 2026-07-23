import type { ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Container, Group, Title, Anchor, Text, Tooltip, ActionIcon, Divider } from "@mantine/core";
import { IconBrandGithub, IconHelp, IconArrowLeft } from "@tabler/icons-react";
import { APP_VERSION, BUILD_INFO } from "../model";
import { UserMenu } from "../auth/UserMenu";
import logo from "../assets/logo.svg";
import wordmark from "../assets/wordmark.svg";

// Shared chrome for every top-level page: wordmark (top-left), optional back + page title, caller
// actions, and the account menu on the right — plus a consistent footer. AppShell offsets the Main
// area by the header height automatically, so no manual padding-top is needed. `container={false}`
// gives full-bleed content (the caller supplies its own layout).
export function PageShell({ title, back = true, actions, children, container = "xl" }: {
  title?: ReactNode; back?: boolean; actions?: ReactNode; children: ReactNode;
  container?: "xl" | "lg" | "md" | "sm" | false;
}) {
  const nav = useNavigate();
  return (
    <AppShell header={{ height: 86 }} footer={{ height: 36 }} padding="lg">
      <AppShell.Header withBorder>
        <Container size="xl" h="100%">
          <Group h="100%" justify="space-between" wrap="nowrap">
            <Group gap="sm" wrap="nowrap">
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
            <Group gap="sm" wrap="nowrap">
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
