import { useState } from "react";
import { ActionIcon, Anchor, Divider, Modal, Stack as MStack, Text, Title, Tooltip } from "@mantine/core";
import { IconHelp } from "@tabler/icons-react";

const DOCS_URL = "https://fgilde.github.io/AspireUI/";

// Self-contained Help button + modal used in both the Stacks overview and
// the Editor header, so each just drops in <HelpButton /> with no shared
// state to wire up.
export function HelpButton() {
  const [opened, setOpened] = useState(false);
  return (
    <>
      <Tooltip label="Help" withArrow>
        <ActionIcon variant="default" size="lg" onClick={() => setOpened(true)} aria-label="Help">
          <IconHelp size={18} />
        </ActionIcon>
      </Tooltip>
      <Modal opened={opened} onClose={() => setOpened(false)} title="How to use AspireUI" size="lg">
        <MStack gap="sm">
          <div>
            <Title order={6}>Create a stack</Title>
            <Text size="sm" c="dimmed">
              Click a resource in the Palette to open its add dialog. Configure it in the Properties
              grid — capabilities (With* and Add* methods, e.g. AddModel on an Ollama resource) and
              References to other resources on the stack.
            </Text>
          </div>
          <div>
            <Title order={6}>Import</Title>
            <Text size="sm" c="dimmed">
              Bring in an existing AppHost from the Import menu: a .cs/.csproj folder or a .zip archive.
            </Text>
          </div>
          <div>
            <Title order={6}>Run a stack</Title>
            <Text size="sm" c="dimmed">
              Run needs Docker (and the .NET SDK) on the machine hosting AspireUI. Use Run in the editor
              header; once it's up, Dashboard opens the Aspire dashboard for full per-resource detail.
            </Text>
          </div>
          <div>
            <Title order={6}>AI assistant</Title>
            <Text size="sm" c="dimmed">
              Configure an AI provider in Settings first, then describe changes in plain language in the
              Assistant panel.
            </Text>
          </div>
          <Divider />
          <Anchor href={DOCS_URL} target="_blank" rel="noreferrer" fw={600}>
            Full documentation →
          </Anchor>
        </MStack>
      </Modal>
    </>
  );
}
