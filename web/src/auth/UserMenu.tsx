import { useNavigate } from "react-router-dom";
import { Button, Menu } from "@mantine/core";
import { IconLogout, IconUser, IconUsers } from "@tabler/icons-react";
import * as api from "../api";
import { useAuth } from "./AuthContext";

// Dropped into both the Stacks overview and Editor headers: current username,
// a Users link for admins (page ships in Task 4 — the link can exist now), Logout.
export function UserMenu() {
  const nav = useNavigate();
  const { status, refresh } = useAuth();
  const user = status?.user;
  if (!user) return null;

  const doLogout = async () => {
    await api.logout();
    await refresh();
    nav("/login");
  };

  return (
    <Menu position="bottom-end" withArrow>
      <Menu.Target>
        <Button variant="default" leftSection={<IconUser size={16} />}>{user.username}</Button>
      </Menu.Target>
      <Menu.Dropdown>
        {user.isAdmin && (
          <Menu.Item leftSection={<IconUsers size={14} />} onClick={() => nav("/users")}>Users</Menu.Item>
        )}
        <Menu.Item leftSection={<IconLogout size={14} />} color="red" onClick={doLogout}>Logout</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
