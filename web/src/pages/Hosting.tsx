import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Group, Title, Button, Container, Table, Badge, Anchor, ActionIcon, Menu, Text } from "@mantine/core";
import { IconArrowLeft, IconDots, IconPlayerPlay, IconPlayerStop, IconTrash, IconExternalLink, IconPencil } from "@tabler/icons-react";
import type { Deployment } from "../model";
import * as api from "../api";
import { useTitle } from "../useTitle";
import { confirmDelete, toastOk, toastErr } from "../ui";

const color = (s: Deployment["state"]) => s === "running" ? "green" : s === "failed" ? "red" : s === "deploying" ? "yellow" : "gray";

export function Hosting() {
  const nav = useNavigate();
  useTitle("Hosting");
  const [items, setItems] = useState<Deployment[]>([]);
  const load = () => api.listHosting().then(setItems).catch(() => {});
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const stop = (d: Deployment) => api.stopHosting(d.stackId).then(load).catch(toastErr);
  const start = (d: Deployment) => api.startHosting(d.stackId).then(load).catch(toastErr);
  const undeploy = (d: Deployment) => confirmDelete(`"${d.name}"`, "This runs docker compose down (named volumes are kept).")
    .then(okd => { if (okd) api.undeployHosting(d.stackId).then(load).then(() => toastOk("Undeployed")).catch(toastErr); });

  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md">
          <Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => nav("/")}>Stacks</Button>
          <Title order={4}>Hosting</Title>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Container size="lg">
          {items.length === 0
            ? <Text c="dimmed" size="sm">No stacks deployed to hosting yet. Open a stack and choose <b>Deploy to hosting</b>.</Text>
            : (
            <Table verticalSpacing="sm">
              <Table.Thead><Table.Tr>
                <Table.Th>App</Table.Th><Table.Th>Status</Table.Th><Table.Th>URLs</Table.Th><Table.Th /></Table.Tr></Table.Thead>
              <Table.Tbody>
                {items.map(d => (
                  <Table.Tr key={d.id}>
                    <Table.Td>{d.name}</Table.Td>
                    <Table.Td><Badge color={color(d.state)} variant="light">{d.state}</Badge></Table.Td>
                    <Table.Td>{d.urls.map(u => <Anchor key={u} href={u} target="_blank" mr="sm" size="sm">{u} <IconExternalLink size={12} /></Anchor>)}</Table.Td>
                    <Table.Td>
                      <Menu position="bottom-end" withArrow>
                        <Menu.Target><ActionIcon variant="subtle" aria-label={`Actions for ${d.name}`}><IconDots size={16} /></ActionIcon></Menu.Target>
                        <Menu.Dropdown>
                          {d.state === "running"
                            ? <Menu.Item leftSection={<IconPlayerStop size={14} />} onClick={() => stop(d)}>Stop</Menu.Item>
                            : <Menu.Item leftSection={<IconPlayerPlay size={14} />} onClick={() => start(d)}>Start</Menu.Item>}
                          <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => nav(`/editor/${d.stackId}`)}>Open in editor</Menu.Item>
                          <Menu.Divider />
                          <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => undeploy(d)}>Undeploy</Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>)}
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
