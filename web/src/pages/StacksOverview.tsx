import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  AppShell, Group, Title, Text, Button, SimpleGrid, Card, ActionIcon,
  Modal, TextInput, Badge, Container, Center, Loader, Stack as MStack, ThemeIcon, Menu,
} from "@mantine/core";
import { IconPlus, IconTrash, IconStack2, IconLayoutGrid, IconChevronDown, IconSparkles } from "@tabler/icons-react";
import type { Stack } from "../model";
import * as api from "../api";
import type { TemplateInfo } from "../api";
import "./StacksOverview.css";

export function StacksOverview() {
  const nav = useNavigate();
  const [stacks, setStacks] = useState<Stack[]>([]);
  const [loading, setLoading] = useState(true);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [templates, setTemplates] = useState<TemplateInfo[]>([]);

  const load = () => api.listStacks().then((s: Stack[]) => { setStacks(s); setLoading(false); });
  useEffect(() => { load(); }, []);
  useEffect(() => { api.getTemplates().then(setTemplates); }, []);

  const create = async () => {
    const s = await api.createStack({ name: name || "New Stack", targetFramework: "net10.0", nodes: [], edges: [], rawStatements: [] });
    setOpen(false); setName("");
    nav(`/stacks/${s.id}`);
  };

  const createDemo = async (templateId: string) => {
    const s = await api.createFromTemplate(templateId);
    nav(`/stacks/${s.id}`);
  };

  return (
    <AppShell header={{ height: 64 }} padding="lg">
      <AppShell.Header withBorder>
        <Container size="xl" h="100%">
          <Group h="100%" justify="space-between">
            <Group gap="sm">
              <ThemeIcon variant="light" size={32} radius="md">
                <IconStack2 size={18} />
              </ThemeIcon>
              <Title order={3} fw={700}>AspireUI</Title>
            </Group>
            <Button.Group>
              <Button leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>
                New Stack
              </Button>
              <Menu position="bottom-end" withArrow>
                <Menu.Target>
                  <Button px="xs" aria-label="Create from demo">
                    <IconChevronDown size={16} />
                  </Button>
                </Menu.Target>
                <Menu.Dropdown>
                  {templates.length === 0 ? (
                    <Menu.Item disabled>No demo templates</Menu.Item>
                  ) : (
                    <>
                      <Menu.Label>From demo…</Menu.Label>
                      {templates.map(t => (
                        <Menu.Item key={t.id} leftSection={<IconSparkles size={14} />}
                          onClick={() => createDemo(t.id)}>
                          {t.name}
                        </Menu.Item>
                      ))}
                    </>
                  )}
                </Menu.Dropdown>
              </Menu>
            </Button.Group>
          </Group>
        </Container>
      </AppShell.Header>

      <AppShell.Main>
        <Container size="xl">
          <Group justify="space-between" mb="lg">
            <div>
              <Title order={2} fw={600}>Stacks</Title>
              <Text c="dimmed" size="sm">Your Aspire hosting projects, ready to open or run.</Text>
            </div>
          </Group>

          {loading ? (
            <Center py={80}>
              <Loader color="indigo" />
            </Center>
          ) : stacks.length === 0 ? (
            <Center py={80}>
              <MStack align="center" gap="xs">
                <ThemeIcon variant="light" size={48} radius="xl" color="gray">
                  <IconLayoutGrid size={24} />
                </ThemeIcon>
                <Text fw={500}>No stacks yet</Text>
                <Text c="dimmed" size="sm" ta="center" maw={320}>
                  Create your first stack to start composing Aspire resources visually.
                </Text>
                <Button mt="sm" leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>
                  New Stack
                </Button>
              </MStack>
            </Center>
          ) : (
            <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="lg">
              {stacks.map(s => (
                <Card
                  key={s.id}
                  withBorder
                  shadow="sm"
                  padding="lg"
                  className="stack-card"
                  style={{ cursor: "pointer" }}
                  onClick={() => nav(`/stacks/${s.id}`)}
                >
                  <Group justify="space-between" wrap="nowrap" align="flex-start">
                    <Text fw={600} lineClamp={1}>{s.name}</Text>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      onClick={async (e) => { e.stopPropagation(); await api.deleteStack(s.id); load(); }}
                      aria-label={`Delete ${s.name}`}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Group>
                  <Group mt="sm" gap="xs">
                    <Badge variant="light" color="indigo">{s.nodes.length} resource{s.nodes.length === 1 ? "" : "s"}</Badge>
                    <Badge variant="outline" color="gray">{s.targetFramework}</Badge>
                  </Group>
                </Card>
              ))}
            </SimpleGrid>
          )}
        </Container>
      </AppShell.Main>

      <Modal opened={open} onClose={() => setOpen(false)} title="New Stack" centered>
        <TextInput
          label="Name"
          placeholder="e.g. checkout-service"
          value={name}
          onChange={e => setName(e.currentTarget.value)}
          onKeyDown={e => { if (e.key === "Enter") create(); }}
          data-autofocus
        />
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setOpen(false)}>Cancel</Button>
          <Button onClick={create}>Create</Button>
        </Group>
      </Modal>
    </AppShell>
  );
}
