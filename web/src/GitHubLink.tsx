import { ActionIcon, Tooltip } from "@mantine/core";
import { IconBrandGithub } from "@tabler/icons-react";

export const REPO_URL = "https://github.com/fgilde/AspireUI";

export function GitHubLink() {
  return (
    <Tooltip label="AspireUI on GitHub" withArrow>
      <ActionIcon component="a" href={REPO_URL} target="_blank" rel="noreferrer"
        variant="default" size="lg" aria-label="GitHub repository">
        <IconBrandGithub size={18} />
      </ActionIcon>
    </Tooltip>
  );
}
