import { useSyncExternalStore } from "react";
import { Badge, Menu, Tooltip } from "@mantine/core";
import { IconApi, IconEye, IconEyeOff } from "@tabler/icons-react";
import { can, PERM_CONFIGURE } from "../model";
import { useAuth } from "../auth/AuthContext";
import * as api from "../api";
import { toastOk, toastErr } from "../ui";

// The retrieve API's state, shared by every menu and badge on the page and read again whenever the
// first of them mounts, so a page change picks up stacks created in between.
type RetrieveInfo = Awaited<ReturnType<typeof api.retrieveInfo>>;
let info: RetrieveInfo | null = null;
const subs = new Set<() => void>();
const load = () => api.retrieveInfo().then(i => { info = i; subs.forEach(f => f()); }).catch(() => {});
const subscribe = (cb: () => void) => {
  if (subs.size === 0) load();
  subs.add(cb);
  return () => { subs.delete(cb); };
};
const useRetrieveInfo = () => useSyncExternalStore(subscribe, () => info);

export function RetrieveMenuItem({ stackId }: { stackId: string }) {
  const i = useRetrieveInfo();
  const user = useAuth().status?.user;
  if (!i?.enabled || !can(user, PERM_CONFIGURE)) return null;
  const hidden = i.hiddenStackIds.includes(stackId);
  const toggle = () => api.setRetrieveHidden(stackId, !hidden)
    .then(() => { toastOk(hidden ? "Listed by the retrieve API again" : "Hidden from the retrieve API"); return load(); })
    .catch(toastErr);
  return (
    <Menu.Item leftSection={hidden ? <IconEye size={14} /> : <IconEyeOff size={14} />} onClick={toggle}>
      {hidden ? "Show in retrieve API" : "Hide from retrieve API"}
    </Menu.Item>
  );
}

// live: whether the app is listed right now — a hosted app always is, a dev run only while it runs.
export function RetrieveBadge({ stackId, live, size = "xs" }: { stackId: string; live: boolean; size?: "xs" | "sm" | "md" | "lg" }) {
  const i = useRetrieveInfo();
  if (!i?.enabled) return null;
  const hidden = i.hiddenStackIds.includes(stackId);
  const how = i.keyRequired ? `with the X-Api-Key header` : "anonymously";
  const label = hidden ? "Hidden from the retrieve API"
    : live ? `Listed by GET ${i.path} (${how})`
    : `Listed by GET ${i.path} once it runs`;
  return (
    <Tooltip label={label} withArrow>
      <Badge size={size} variant={live && !hidden ? "light" : "outline"} color={hidden ? "gray" : "teal"}
        leftSection={<IconApi size={size === "lg" ? 14 : 10} />}>
        {hidden ? "API hidden" : "API"}
      </Badge>
    </Tooltip>
  );
}
